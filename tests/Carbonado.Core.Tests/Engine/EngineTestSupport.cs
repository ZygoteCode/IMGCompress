using Carbonado.Core.Codecs;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Engine;

/// <summary>Builders for engine tests: a context over the repository's tools in a private temp root, inputs, runs.</summary>
internal static class EngineTestSupport
{
    /// <summary>The encoders and decoders the default profiles race.</summary>
    public static readonly ToolId[] CoreTools =
        [ToolId.Cjxl, ToolId.Djxl, ToolId.Avifenc, ToolId.Avifdec, ToolId.Cwebp, ToolId.Dwebp, ToolId.Oxipng, ToolId.Ect, ToolId.Pngquant];

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Skips the test unless every tool is installed (all core tools when none are named).</summary>
    public static void RequireTools(params ToolId[] tools)
    {
        foreach (ToolId tool in tools.Length == 0 ? CoreTools : tools)
        {
            TestTools.Require(tool);
        }
    }

    /// <summary>An engine context whose session folder lives inside <paramref name="dir"/>.</summary>
    public static EngineContext Context(TempDir dir, EngineOptions? options = null) =>
        EngineContext.Create(FormatFixtures.Locator, (options ?? new EngineOptions()) with { TempRoot = dir.Sub("engine-temp") });

    public static string WritePng(string path, PixelBuffer px, ImageMetadata? metadata = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        PngEncoder.EncodeFast(fs, px, metadata);
        return path;
    }

    /// <summary>The first Windows wallpaper photo, scaled to fit <paramref name="maxWidth"/> and saved as a quality-92 JPEG (skips without one).</summary>
    public static string PhotoJpeg(TempDir dir, string name = "photo.jpg", int maxWidth = 800)
    {
        var photos = TestImages.SystemPhotos(1);
        if (photos.Count == 0)
        {
            Assert.Skip("No system photo in C:\\Windows\\Web.");
        }

        using var full = TestImages.LoadWithWic(photos[0]);
        var (w, h) = Resampler.FitWithin(full.Width, full.Height, maxWidth, maxWidth);
        using var small = Resampler.Resize(full, w, h);
        string path = dir.File(name);
        TestImages.SaveJpegWithWic(small, path, 92);
        return path;
    }

    public static Task<CompressionResult> RunAsync(EngineContext ctx, string input, CompressionProfile profile, OutputPlan? plan = null, List<JobProgress>? progress = null, string? inputRoot = null, CancellationToken? ct = null)
    {
        var engine = new CompressionEngine(ctx);
        IProgress<JobProgress>? reporter = progress is null ? null : new SyncProgress<JobProgress>(p =>
        {
            lock (progress)
            {
                progress.Add(p);
            }
        });
        return engine.RunAsync(CompressionJob.Create(input, profile, plan ?? new OutputPlan(), inputRoot), reporter, Environment.ProcessorCount, ct ?? Ct)
            .ContinueWith(t => Describe(t.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Writes the result and every candidate attempt to the test output (visible with -showLiveOutput).</summary>
    public static CompressionResult Describe(CompressionResult r)
    {
        var output = TestContext.Current.TestOutputHelper;
        if (output is null)
        {
            return r;
        }

        output.WriteLine(FormattableString.Invariant($"{Path.GetFileName(r.InputPath)}: {r.Outcome} {r.Format} via {r.EncoderId} {r.OriginalBytes} -> {r.OutputBytes} B score {r.Score:0.00} {r.Verification} {r.Elapsed.TotalSeconds:0.00} s {r.Error}"));
        foreach (var c in r.Candidates)
        {
            output.WriteLine(FormattableString.Invariant($"   {c.EncoderId,-14} {c.Kind,-13} p={c.Parameter,-6} {c.Bytes,8} B score {c.Score:0.00} {(c.Passed ? "pass" : "fail")} {c.Elapsed.TotalMilliseconds:0} ms"));
        }

        return r;
    }

    /// <summary>Files under <paramref name="dir"/> except the engine's temp root.</summary>
    public static string[] UserFiles(TempDir dir) =>
        Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(dir.Path, "engine-temp"), StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(dir.Path, f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Delivers progress synchronously on the reporting thread (<see cref="Progress{T}"/> would post it).</summary>
    public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
