using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>png.oxipng</c>: lossless PNG optimisation of the PNG intermediate (which already holds exactly the policy
/// metadata); animated PNGs are optimised from the original's frames, with the metadata replaced by the policy's, so
/// no frame is lost and nothing the policy removes survives.
/// </summary>
internal sealed class OxipngAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    public override string Id => "png.oxipng";

    public override OutputFormat Format => OutputFormat.Png;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Oxipng];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossless, EncodeKind.Optimize];

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossless => true,
        EncodeKind.Optimize => source is ImageFormat.Png or ImageFormat.Apng,
        _ => false,
    };

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string> { "-o", ByEffort(r.Effort, "2", "4", "6", "max") };
        args.AddRange(PngStrip.OxipngArguments(r.Source));
        if (r.Source.Analysis.HasAlpha && !r.Source.PreserveInvisibleRgb)
        {
            args.Add("-a");
        }

        if (r.Effort == EffortLevel.Insane)
        {
            args.Add("-z");
        }

        args.AddRange(["-t", Number(Math.Max(1, r.Threads)), "-q", "--out", r.OutputPath, inputPath]);
        return args;
    }

    protected override bool PreservesAnimation(EncodeRequest r) => r.Source.OriginalFormat == ImageFormat.Apng;

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = Name(await PngStrip.InputAsync(r.Source, ct).ConfigureAwait(false));
        await RunAsync(r, ToolId.Oxipng, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
    }
}

/// <summary>Input selection and metadata stripping rules shared by the PNG optimisers.</summary>
internal static class PngStrip
{
    /// <summary>True when nothing beyond pixels must survive (no ICC/EXIF/XMP, default DPI).</summary>
    public static bool NothingToKeep(PreparedSource s) =>
        s.Metadata.Icc is null && s.Metadata.Exif is null && s.Metadata.Xmp is null && s.Metadata.DpiX == 96 && s.Metadata.DpiY == 96;

    /// <summary>
    /// oxipng's strip mode: everything when there is nothing to keep, "safe" (keeps iCCP/pHYs) when only the colour
    /// profile or density matter, nothing when EXIF/XMP must be kept or the source is animated (the input then holds
    /// exactly the policy metadata, plus the stored EXIF orientation of unrotated frames, which "safe" would drop).
    /// </summary>
    public static string[] OxipngArguments(PreparedSource s) =>
        s.IsAnimated || s.Metadata.Exif is not null || s.Metadata.Xmp is not null ? []
            : NothingToKeep(s) ? ["--strip", "all"]
            : ["--strip", "safe"];

    /// <summary>
    /// The PNG to optimise: the intermediate, or for animations the original APNG's frames (never flattened) carrying
    /// exactly the policy metadata.
    /// </summary>
    public static Task<string> InputAsync(PreparedSource s, CancellationToken ct) =>
        s.IsAnimated ? s.GetAnimatedPngAsync(ct) : s.GetPngAsync(ct);
}
