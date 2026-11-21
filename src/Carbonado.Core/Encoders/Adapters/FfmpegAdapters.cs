using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// Fallback encoders through ffmpeg's libjxl/libaom/libwebp, registered only when the primary tool is missing.
/// They read the PNG intermediate. Colour profiles: WebP gets its metadata written afterwards; JPEG XL and AVIF
/// through ffmpeg carry no ICC profile (sources tagged with a non-sRGB profile should prefer the primary tools).
/// </summary>
internal abstract class FfmpegAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Ffmpeg];

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", inputPath };
        args.AddRange(CodecArguments(r));
        args.AddRange(["-threads", Number(Math.Max(1, r.Threads)), r.OutputPath]);
        return args;
    }

    /// <summary>The codec part of the command line (between the input and the output).</summary>
    protected abstract IEnumerable<string> CodecArguments(EncodeRequest r);

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = Name(await r.Source.GetPngAsync(ct).ConfigureAwait(false));
        await RunAsync(r, ToolId.Ffmpeg, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
    }
}

/// <summary><c>jxl.ffmpeg</c>: JPEG XL through ffmpeg's libjxl (distance scale like cjxl).</summary>
internal sealed class FfmpegJxlAdapter(ToolLocator tools, ProcessRunner runner) : FfmpegAdapter(tools, runner)
{
    public override string Id => "jxl.ffmpeg";

    public override OutputFormat Format => OutputFormat.Jxl;

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless];

    public override ParameterScale Scale => CjxlAdapter.DistanceScale;

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind is EncodeKind.Lossy or EncodeKind.Lossless;

    public override double InitialGuess(double targetScore, ImageAnalysis a) => CjxlAdapter.DistanceFor(targetScore);

    protected override IEnumerable<string> CodecArguments(EncodeRequest r) =>
        ["-c:v", "libjxl", "-distance", r.Kind == EncodeKind.Lossless ? "0" : Number(Scale.Normalize(r.Parameter ?? 1)), "-effort", Number(ByEffort(r.Effort, 5, 7, 9, 9))];
}

/// <summary><c>avif.ffmpeg</c>: AVIF through ffmpeg's libaom (CRF 0–63, lower is better; opaque images only).</summary>
internal sealed class FfmpegAvifAdapter(ToolLocator tools, ProcessRunner runner) : FfmpegAdapter(tools, runner)
{
    // SSIMULACRA2 → CRF, derived from the avifenc calibration (quality q ≈ CRF 63·(100 − q)/100).
    private static readonly (double, double)[] s_crf =
        [(30, 60), (40, 55), (50, 50), (60, 44), (70, 38), (75, 35), (80, 31), (85, 24), (90, 13), (95, 4), (99, 1)];

    public override string Id => "avif.ffmpeg";

    public override OutputFormat Format => OutputFormat.Avif;

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy];

    public override ParameterScale Scale { get; } = new(0, 63, HigherIsBetter: false, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind == EncodeKind.Lossy && !a.HasAlpha;

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(s_crf, targetScore));

    protected override IEnumerable<string> CodecArguments(EncodeRequest r)
    {
        int crf = (int)Scale.Normalize(r.Parameter ?? 30);
        bool full = r.Chroma switch
        {
            ChromaMode.Yuv444 => true,
            ChromaMode.Yuv420 => false,
            _ => crf <= 24,
        };
        return ["-c:v", "libaom-av1", "-crf", Number(crf), "-cpu-used", Number(ByEffort(r.Effort, 8, 6, 3, 1)), "-still-picture", "1", "-pix_fmt", full ? "yuv444p10le" : "yuv420p10le"];
    }
}

/// <summary><c>webp.ffmpeg</c>: WebP through ffmpeg's libwebp (metadata written into the container afterwards).</summary>
internal sealed class FfmpegWebPAdapter(ToolLocator tools, ProcessRunner runner) : FfmpegAdapter(tools, runner)
{
    public override string Id => "webp.ffmpeg";

    public override OutputFormat Format => OutputFormat.WebP;

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossy => true,
        EncodeKind.Lossless => !a.Has16BitPrecision,
        _ => false,
    };

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(CwebpAdapter.Quality, targetScore));

    protected override IEnumerable<string> CodecArguments(EncodeRequest r) => r.Kind == EncodeKind.Lossless
        ? ["-c:v", "libwebp", "-lossless", "1", "-compression_level", "6", "-quality", "100", "-pix_fmt", "bgra"]
        : ["-c:v", "libwebp", "-quality", Number((int)Scale.Normalize(r.Parameter ?? 75)), "-compression_level", Number(ByEffort(r.Effort, 4, 6, 6, 6)), "-pix_fmt", r.Source.Analysis.HasAlpha ? "yuva420p" : "yuv420p"];

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        await base.ProduceAsync(r, outputName, steps, ct).ConfigureAwait(false);
        await CwebpAdapter.ApplyMetadataAsync(r, outputName, steps, ct).ConfigureAwait(false);
    }
}
