using System.Globalization;
using System.Text;
using System.Text.Json;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tools;

namespace Carbonado.Cli;

/// <summary>
/// Runs a <c>carbonado-cli</c> command line: expands the inputs, compresses them with the same engine and scheduler as
/// the app, and prints one line (or one JSON object) per file as each finishes.
/// </summary>
internal static class CliRunner
{
    private const char Minus = '−';
    private const char Arrow = '→';

    /// <summary>Runs the command line and returns the exit code: 0 no failure, 1 some file failed (or the run was cancelled), 2 usage error.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        CliOptions options;
        CompressionProfile profile;
        try
        {
            options = CliOptions.Parse(args);
            if (options.Help)
            {
                stdout.Write(CliOptions.Usage);
                return 0;
            }

            profile = options.BuildProfile();
        }
        catch (CliUsageException ex)
        {
            stderr.WriteLine($"carbonado: {ex.Message}");
            stderr.WriteLine("Run 'carbonado --help' for usage.");
            return 2;
        }

        OutputPlan plan = options.BuildOutputPlan();
        var (inputs, missing) = InputScanner.Expand(options.Paths, options.Recursive, plan);
        var printer = new Printer(stdout, options.Json);
        foreach (string path in missing)
        {
            printer.Missing(path);
        }

        if (options.DryRun)
        {
            foreach (var input in inputs)
            {
                printer.DryRun(input.Path, OutputPathResolver.ResolveFolder(input.Path, input.Root, plan), profile.Id);
            }

            return missing.Count > 0 ? 1 : 0;
        }

        if (inputs.Count == 0)
        {
            stderr.WriteLine(missing.Count > 0 ? "carbonado: nothing to compress." : "carbonado: no images found.");
            return missing.Count > 0 ? 1 : 0;
        }

        DataPaths data = DataPaths.Resolve(options.DataDir, AppContext.BaseDirectory);
        bool ownsLog = !Log.IsInitialized;
        try
        {
            Directory.CreateDirectory(data.TempRoot);
            SessionTemp.CleanupStale(data.TempRoot, TimeSpan.FromDays(2));
            if (ownsLog)
            {
                Log.Init(data.LogsDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"carbonado: the data folder {data.LocalRoot} is not writable ({ex.Message}).");
            return 2;
        }

        try
        {
            var tools = new ToolLocator(ToolLocator.DefaultOptions(AppContext.BaseDirectory));
            using EngineContext context = EngineContext.Create(tools, new EngineOptions(TempRoot: data.TempRoot));
            if (!context.Encoders.Available.Any(e => e.Id is "jxl.cjxl" or "avif.avifenc" or "webp.cwebp"))
            {
                stderr.WriteLine("carbonado: warning: no JPEG XL, AVIF or WebP encoder was found (run build/fetch-tools.ps1); only PNG optimization is available.");
            }

            return await RunBatchAsync(context, inputs, profile, plan, printer, missing.Count, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsLog)
            {
                Log.Flush();
            }
        }
    }

    /// <summary>A compressed file as a text line, e.g. "ok  photo.jpg → photo.avif  1.20 MB → 143 KB  −88%  VS 86.9".</summary>
    internal static string FormatLine(CompressionResult r)
    {
        string name = Path.GetFileName(r.InputPath);
        string original = FormatBytes(r.OriginalBytes);
        return r.Outcome switch
        {
            JobOutcome.Compressed => $"ok  {name} {Arrow} {Path.GetFileName(r.OutputPath)}  {original} {Arrow} {FormatBytes(r.OutputBytes)}  {Change(r)}  {Quality(r)}",
            JobOutcome.KeptOriginal => $"kept  {name}  {original}  {r.Error}",
            JobOutcome.Skipped => $"skip  {name}  {r.Error}",
            JobOutcome.Failed => $"fail  {name}  {r.Error}",
            _ => $"cancelled  {name}",
        };
    }

    /// <summary>"1.20 MB", "143 KB", "9.8 KB", "512 B" (binary units, invariant culture).</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>The quality label: the clarity grade reached by the measured score ("VS 86.9"), or how exactness was proven.</summary>
    internal static string Quality(CompressionResult r) => r.Verification switch
    {
        VerificationKind.BitExactReconstruction => "FL bit-exact",
        VerificationKind.PixelExact => "FL pixel-exact",
        _ when r.Score is { } s => (Grade(s) + " " + s.ToString("0.0", CultureInfo.InvariantCulture)).TrimStart(),
        _ => string.Empty,
    };

    private static string Grade(double score) => score switch
    {
        >= 90 => "VVS",
        >= 85 => "VS",
        >= 78 => "SI",
        >= 65 => "I",
        _ => string.Empty,
    };

    private static string Change(CompressionResult r)
    {
        if (r.OriginalBytes <= 0)
        {
            return string.Empty;
        }

        double change = (r.OutputBytes - r.OriginalBytes) * 100.0 / r.OriginalBytes;
        int rounded = (int)Math.Round(Math.Abs(change), MidpointRounding.AwayFromZero);
        return change <= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Minus}{rounded}%")
            : string.Create(CultureInfo.InvariantCulture, $"+{rounded}%");
    }

    private static async Task<int> RunBatchAsync(EngineContext context, IReadOnlyList<InputScanner.Input> inputs, CompressionProfile profile, OutputPlan plan, Printer printer, int missing, CancellationToken ct)
    {
        var engine = new CompressionEngine(context);
        await using var scheduler = new BatchScheduler(engine, context.Options);
        var finished = new TaskCompletionSource<BatchSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failed = 0;
        scheduler.JobCompleted += (_, r) =>
        {
            if (r.Outcome == JobOutcome.Failed)
            {
                Interlocked.Increment(ref failed);
            }

            printer.Result(r);
        };
        scheduler.BatchCompleted += (_, summary) => finished.TrySetResult(summary);

        foreach (var input in inputs)
        {
            scheduler.Enqueue(CompressionJob.Create(input.Path, profile, plan, input.Root));
        }

        using (ct.Register(scheduler.CancelAll))
        {
            scheduler.Start();
            BatchSummary summary = await finished.Task.ConfigureAwait(false);
            printer.Summary(summary, missing);
            return failed + missing > 0 || summary.Cancelled > 0 ? 1 : 0;
        }
    }

    /// <summary>Writes results as text lines or JSON lines, one at a time (scheduler events arrive in order on one thread).</summary>
    private sealed class Printer(TextWriter output, bool json)
    {
        private readonly Lock _gate = new();

        public void Result(CompressionResult r) => Write(json ? Json(r) : FormatLine(r));

        public void Missing(string path) => Write(json
            ? Json(w =>
            {
                w.WriteString("input", path);
                w.WriteString("outcome", "failed");
                w.WriteString("error", "The file does not exist.");
            })
            : $"fail  {path}  The file does not exist.");

        public void DryRun(string path, string folder, string profileId) => Write(json
            ? Json(w =>
            {
                w.WriteString("input", path);
                w.WriteString("outputFolder", folder);
                w.WriteString("profile", profileId);
                w.WriteBoolean("dryRun", true);
            })
            : $"would  {path}  {Arrow} {folder}");

        public void Summary(BatchSummary s, int missing)
        {
            if (json)
            {
                return;
            }

            string saved = s.OriginalBytes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"  {FormatBytes(s.OriginalBytes)} {Arrow} {FormatBytes(s.OutputBytes)} ({Minus}{s.SavedFraction * 100:0}%)")
                : string.Empty;
            Write(string.Create(CultureInfo.InvariantCulture,
                $"{s.Total + missing} file(s): {s.Compressed} compressed, {s.Kept} kept, {s.Failed + missing} failed{(s.Cancelled > 0 ? $", {s.Cancelled} cancelled" : string.Empty)}{saved} in {s.Elapsed.TotalSeconds:0.0} s"));
        }

        private static string Json(CompressionResult r) => Json(w =>
        {
            w.WriteString("input", r.InputPath);
            WriteNullable(w, "output", r.OutputPath);
            w.WriteString("outcome", JsonNamingPolicy.CamelCase.ConvertName(r.Outcome.ToString()));
            WriteNullable(w, "format", r.Format?.ToString().ToLowerInvariant());
            WriteNullable(w, "encoder", r.EncoderId);
            w.WriteNumber("originalBytes", r.OriginalBytes);
            w.WriteNumber("outputBytes", r.OutputBytes);
            w.WriteNumber("savedFraction", Math.Round(r.SavedFraction, 4));
            if (r.Score is { } score)
            {
                w.WriteNumber("score", Math.Round(score, 2));
            }
            else
            {
                w.WriteNull("score");
            }

            w.WriteString("verification", JsonNamingPolicy.CamelCase.ConvertName(r.Verification.ToString()));
            w.WriteNumber("width", r.Width);
            w.WriteNumber("height", r.Height);
            w.WriteNumber("seconds", Math.Round(r.Elapsed.TotalSeconds, 3));
            WriteNullable(w, r.Outcome == JobOutcome.Failed ? "error" : "note", r.Error);
        });

        private static string Json(Action<Utf8JsonWriter> body)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
        {
            if (value is null)
            {
                w.WriteNull(name);
            }
            else
            {
                w.WriteString(name, value);
            }
        }

        private void Write(string line)
        {
            lock (_gate)
            {
                output.WriteLine(line);
                output.Flush();
            }
        }
    }
}
