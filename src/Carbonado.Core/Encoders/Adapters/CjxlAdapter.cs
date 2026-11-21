using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>jxl.cjxl</c>: JPEG XL lossy (distance), lossless (modular) and JPEG → JPEG XL lossless transcode. Input is the
/// PAM intermediate with ICC/EXIF/XMP attached through <c>-x</c>; animations are encoded from the original frames (an
/// APNG through its policy-filtered copy, since cjxl copies PNG metadata).
/// </summary>
internal sealed class CjxlAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    // SSIMULACRA2 → distance, calibrated with ssimulacra2 on photos at effort 7 (d 1 ≈ 89, d 2 ≈ 83, d 3 ≈ 79, d 6 ≈ 65).
    private static readonly (double, double)[] s_distance =
        [(30, 15), (40, 12.5), (50, 10), (60, 7.3), (65, 6), (70, 4.75), (75, 3.75), (80, 2.75), (85, 1.7), (90, 0.85), (92, 0.5), (95, 0.25), (99, 0.05)];

    public override string Id => "jxl.cjxl";

    public override OutputFormat Format => OutputFormat.Jxl;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Cjxl];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless, EncodeKind.JpegTranscode];

    /// <summary>Butteraugli distance: 0.05 (best) … 15, step 0.01.</summary>
    internal static ParameterScale DistanceScale { get; } = new(0.05, 15, HigherIsBetter: false, Resolution: 0.01);

    public override ParameterScale Scale => DistanceScale;

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossy or EncodeKind.Lossless => true,
        EncodeKind.JpegTranscode => source == ImageFormat.Jpeg,
        _ => false,
    };

    public override double InitialGuess(double targetScore, ImageAnalysis a) => DistanceFor(targetScore);

    /// <summary>The distance expected to reach <paramref name="targetScore"/>.</summary>
    internal static double DistanceFor(double targetScore) => DistanceScale.Normalize(Interpolate(s_distance, targetScore));

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string> { inputPath, r.OutputPath };
        switch (r.Kind)
        {
            case EncodeKind.Lossy:
                args.AddRange(["-d", Number(Scale.Normalize(r.Parameter ?? 1)), "-e", Number(ByEffort(r.Effort, 5, 7, 9, 10))]);
                break;

            case EncodeKind.Lossless:
                args.AddRange(["-d", "0", "-e", Number(ByEffort(r.Effort, 7, 9, 10, 10))]);
                if (r.Effort == EffortLevel.Insane)
                {
                    args.AddRange(["-I", "100", "-g", "3"]);
                }

                if (r.Source.Analysis.HasAlpha && !r.Source.PreserveInvisibleRgb)
                {
                    args.Add("--keep_invisible=0");
                }

                break;

            default:
                args.AddRange(["--lossless_jpeg=1", "-e", Number(ByEffort(r.Effort, 7, 9, 9, 10))]);
                break;
        }

        args.Add($"--num_threads={Math.Max(1, r.Threads)}");
        if (UsesIntermediate(r))
        {
            ImageMetadata m = r.Source.Metadata;
            if (m.Icc is { Length: > 0 })
            {
                args.AddRange(["-x", "icc_pathname=" + PreparedSource.IccName]);
            }

            if (m.Exif is { Length: > 0 })
            {
                args.AddRange(["-x", "exif=" + PreparedSource.ExifName]);
            }

            if (m.Xmp is { Length: > 0 })
            {
                args.AddRange(["-x", "xmp=" + PreparedSource.XmpName]);
            }
        }

        args.Add("--quiet");
        return args;
    }

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input;
        if (r.Kind == EncodeKind.JpegTranscode)
        {
            // The policy-filtered JPEG (or a work-dir copy of the original when nothing changes).
            string reference = await r.Source.GetJpegReferenceAsync(ct).ConfigureAwait(false);
            input = string.Equals(reference, r.Source.OriginalPath, StringComparison.OrdinalIgnoreCase)
                ? Name(await r.Source.GetOriginalCopyAsync(ct).ConfigureAwait(false))
                : Name(reference);
        }
        else if (r.Source.IsAnimated)
        {
            input = Name(r.Source.OriginalFormat == ImageFormat.Apng
                ? await r.Source.GetAnimatedPngAsync(ct).ConfigureAwait(false)
                : await r.Source.GetOriginalCopyAsync(ct).ConfigureAwait(false));
        }
        else
        {
            input = Name(await r.Source.GetPamAsync(ct).ConfigureAwait(false));
            await r.Source.GetIccPathAsync().ConfigureAwait(false);
            await r.Source.GetExifPathAsync().ConfigureAwait(false);
            await r.Source.GetXmpPathAsync().ConfigureAwait(false);
        }

        await RunAsync(r, ToolId.Cjxl, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
    }

    // cjxl reads GIF and APNG animations directly.
    protected override bool PreservesAnimation(EncodeRequest r) => r.Source.OriginalFormat is ImageFormat.Gif or ImageFormat.Apng;

    private static bool UsesIntermediate(EncodeRequest r) => r.Kind != EncodeKind.JpegTranscode && !r.Source.IsAnimated;
}
