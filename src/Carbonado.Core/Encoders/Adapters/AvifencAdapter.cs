using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>avif.avifenc</c>: AVIF (libaom) lossy at 10 bits — 4:4:4 for high qualities, sharp-YUV 4:2:0 below, 4:0:0 for
/// gray images — and lossless (8-bit sources only; AVIF cannot hold 16-bit precision). Input is the PNG intermediate;
/// ICC/EXIF/XMP are passed as files.
/// </summary>
internal sealed class AvifencAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    /// <summary>
    /// With <see cref="ChromaMode.Auto"/>, qualities from here up keep full-resolution chroma (≈ SSIMULACRA2 83+); below
    /// it sharp-YUV 4:2:0 is a little smaller at equal score, above it 4:2:0 costs bytes (measured).
    /// </summary>
    internal const int AutoFullChromaQuality = 60;

    // SSIMULACRA2 → quality, calibrated on photos at speed 6, 10-bit (q 45 ≈ 76, q 55 ≈ 82, q 65 ≈ 86, q 75 ≈ 89).
    private static readonly (double, double)[] s_quality =
        [(30, 5), (40, 12), (50, 20), (60, 30), (65, 34), (70, 39), (75, 44), (80, 51), (85, 62), (88, 71), (90, 79), (92, 87), (95, 94), (99, 99)];

    public override string Id => "avif.avifenc";

    public override OutputFormat Format => OutputFormat.Avif;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Avifenc];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossy => true,
        EncodeKind.Lossless => !a.Has16BitPrecision,
        _ => false,
    };

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(s_quality, targetScore));

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        string speed = Number(ByEffort(r.Effort, 8, 6, 3, 1));
        var args = new List<string>();
        if (r.Kind == EncodeKind.Lossless)
        {
            args.AddRange(["-l", "-d", "8", "-s", speed]);
        }
        else
        {
            int q = (int)Scale.Normalize(r.Parameter ?? 60);
            args.AddRange(["-q", Number(q), "-s", speed, "-d", "10"]);
            bool full = r.Chroma switch
            {
                ChromaMode.Yuv444 => true,
                ChromaMode.Yuv420 => false,
                _ => q >= AutoFullChromaQuality,
            };

            // 4:0:0 only when no RGB profile describes the data (a gray image tagged with an RGB profile stays RGB).
            if (r.Source.Analysis.IsGrayscale && !IccHeader.IsRgb(r.Source.Metadata.Icc))
            {
                args.AddRange(["-y", "400"]);
            }
            else if (full)
            {
                args.AddRange(["-y", "444"]);
            }
            else
            {
                args.AddRange(["-y", "420", "--sharpyuv"]);
            }
        }

        args.AddRange(["-j", Number(Math.Max(1, r.Threads))]);

        // Metadata comes from explicit files (each implies ignoring the PNG's own copy), or is suppressed.
        ImageMetadata m = r.Source.Metadata;
        args.AddRange(m.Icc is { Length: > 0 } ? ["--icc", PreparedSource.IccName] : ["--ignore-icc"]);
        args.AddRange(m.Exif is { Length: > 0 } ? ["--exif", PreparedSource.ExifName] : ["--ignore-exif"]);
        args.AddRange(m.Xmp is { Length: > 0 } ? ["--xmp", PreparedSource.XmpName] : ["--ignore-xmp"]);

        args.AddRange(["--", inputPath, r.OutputPath]);
        return args;
    }

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = Name(await r.Source.GetPngAsync(ct).ConfigureAwait(false));
        await r.Source.GetIccPathAsync().ConfigureAwait(false);
        await r.Source.GetExifPathAsync().ConfigureAwait(false);
        await r.Source.GetXmpPathAsync().ConfigureAwait(false);
        await RunAsync(r, ToolId.Avifenc, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
    }
}
