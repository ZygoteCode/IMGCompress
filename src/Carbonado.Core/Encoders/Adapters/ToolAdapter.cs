using System.Diagnostics;
using System.Globalization;
using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// Shared plumbing of the encoder adapters: every step runs with the source's work directory as current directory and
/// short relative file names; the result is produced under a temporary name there and moved to the requested output
/// only when complete (never over the source file); failures — a tool that fails, cannot start or was disposed, an
/// unsupported input, a file error — become <see cref="EncodeOutcome.Error"/>, cancellation propagates.
/// </summary>
internal abstract class ToolAdapter(ToolLocator tools, ProcessRunner runner) : IEncoderAdapter
{
    protected ToolLocator Tools { get; } = tools;

    protected ProcessRunner Runner { get; } = runner;

    public abstract string Id { get; }

    public abstract OutputFormat Format { get; }

    public abstract IReadOnlyList<ToolId> RequiredTools { get; }

    public abstract IReadOnlyCollection<EncodeKind> Kinds { get; }

    public virtual ParameterScale Scale => ParameterScale.None;

    public abstract bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source);

    public virtual double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Min;

    public abstract IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath);

    public async Task<EncodeOutcome> EncodeAsync(EncodeRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var clock = Stopwatch.StartNew();
        var steps = new List<string>();
        string temp = Path.Combine(r.Source.WorkDir, $"out-{Id.Replace('.', '-')}-{Guid.NewGuid():N}{FormatInfo.Extension(Format)}");
        try
        {
            if (!Kinds.Contains(r.Kind) || !Supports(r.Kind, r.Source.Analysis, r.Source.OriginalFormat))
            {
                throw new EncodeFailedException($"{Id} cannot produce {r.Kind} output for this {r.Source.OriginalFormat} image.");
            }

            if (r.Kind == EncodeKind.Lossy && r.Parameter is null)
            {
                throw new EncodeFailedException($"{Id}: a quality parameter is required for lossy encoding.");
            }

            if (r.Source.IsAnimated && !PreservesAnimation(r))
            {
                throw new EncodeFailedException($"{Id} would flatten the animated {r.Source.OriginalFormat}; it is left to animation-capable encoders.");
            }

            if (string.Equals(Path.GetFullPath(r.OutputPath), Path.GetFullPath(r.Source.OriginalPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new EncodeFailedException($"{Id}: the output path is the source file itself, which is never overwritten.");
            }

            await ProduceAsync(r, Path.GetFileName(temp), steps, ct).ConfigureAwait(false);
            var produced = new FileInfo(temp);
            if (!produced.Exists || produced.Length == 0)
            {
                throw new EncodeFailedException($"{Id} produced no output.");
            }

            long bytes = produced.Length;
            Commit(temp, r.OutputPath);
            return new EncodeOutcome(true, r.OutputPath, bytes, clock.Elapsed, string.Join(" && ", steps), null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (IsEncodeFailure(ex))
        {
            TryDelete(temp);
            return new EncodeOutcome(false, r.OutputPath, 0, clock.Elapsed, string.Join(" && ", steps), ex.Message);
        }
    }

    /// <summary>
    /// The failures reported as an outcome rather than thrown: tool and input errors, file errors, a tool that cannot be
    /// started (<see cref="InvalidOperationException"/>, which also covers an <see cref="ObjectDisposedException"/> from a
    /// disposed runner or source) and unsupported requests. Cancellation is not among them.
    /// </summary>
    internal static bool IsEncodeFailure(Exception ex) =>
        ex is EncodeFailedException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or InvalidOperationException or NotSupportedException;

    /// <summary>True when the adapter encodes all frames of an animated source (from the original file).</summary>
    protected virtual bool PreservesAnimation(EncodeRequest r) => false;

    /// <summary>Writes the result to <paramref name="outputName"/> inside the work directory, appending each command to <paramref name="steps"/>.</summary>
    protected abstract Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct);

    /// <summary>Runs one tool step in the work directory; a non-zero exit code or a timeout fails the encode.</summary>
    protected async Task RunAsync(EncodeRequest r, ToolId tool, IReadOnlyList<string> args, List<string> steps, CancellationToken ct)
    {
        string exe = Tools.Get(tool)?.Path ?? throw new EncodeFailedException($"{ToolLocator.Layout(tool).FileName} is not installed.");
        steps.Add(Display(exe, args));
        var result = await Runner.RunAsync(new ProcessSpec(exe, args, r.Source.WorkDir, Timeout(r)), ct).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new EncodeFailedException($"{Path.GetFileNameWithoutExtension(exe)} timed out after {Timeout(r).TotalMinutes:0} min.");
        }

        if (result.ExitCode != 0)
        {
            throw new EncodeFailedException($"{Path.GetFileNameWithoutExtension(exe)} failed (exit code {result.ExitCode}){Tail(result.Stderr)}");
        }
    }

    /// <summary>Runs managed work (no tool) as a step, for the command-line log.</summary>
    protected static Task RunManagedAsync(string description, List<string> steps, Action work, CancellationToken ct)
    {
        steps.Add(description);
        return Task.Run(work, ct);
    }

    protected static string Name(string fullPath) => Path.GetFileName(fullPath);

    protected static string Number(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    protected static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The value of a per-effort table (Fast, Balanced, Max, Insane).</summary>
    protected static T ByEffort<T>(EffortLevel effort, T fast, T balanced, T max, T insane) => effort switch
    {
        EffortLevel.Fast => fast,
        EffortLevel.Balanced => balanced,
        EffortLevel.Max => max,
        _ => insane,
    };

    /// <summary>Piecewise-linear interpolation in a (score → parameter) table sorted by score, clamped at both ends.</summary>
    protected static double Interpolate(ReadOnlySpan<(double Score, double Parameter)> table, double score)
    {
        if (score <= table[0].Score)
        {
            return table[0].Parameter;
        }

        for (int i = 1; i < table.Length; i++)
        {
            if (score <= table[i].Score)
            {
                var (s0, p0) = table[i - 1];
                var (s1, p1) = table[i];
                return p0 + ((p1 - p0) * (score - s0) / (s1 - s0));
            }
        }

        return table[^1].Parameter;
    }

    protected string TempName(string extension) => $"tmp-{Id.Replace('.', '-')}-{Guid.NewGuid():N}{extension}";

    protected static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Generous per-megapixel budget by effort; the timeout only guards against hung tools.
    private static TimeSpan Timeout(EncodeRequest r)
    {
        double megapixels = Math.Max(1, r.Source.Pixels.PixelCount / 1e6);
        double perMegapixel = ByEffort(r.Effort, 15.0, 30.0, 90.0, 300.0);
        return TimeSpan.FromSeconds(Math.Clamp(120 + (megapixels * perMegapixel), 120, 4 * 3600));
    }

    private static string Tail(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string tail = string.Join(" ", lines.TakeLast(3));
        return tail.Length == 0 ? "." : ": " + (tail.Length > 400 ? tail[^400..] : tail);
    }

    private static void Commit(string temp, string output)
    {
        string full = Path.GetFullPath(output);
        if (string.Equals(full, Path.GetFullPath(temp), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Path.GetDirectoryName(full) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        File.Move(temp, full, overwrite: true);
    }

    private static string Display(string exe, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}

/// <summary>An expected encode failure (tool error, unsupported input), reported through <see cref="EncodeOutcome.Error"/>.</summary>
internal sealed class EncodeFailedException(string message) : Exception(message);
