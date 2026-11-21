using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>jpeg.sjpeg</c>: JPEG through djxl's sjpeg encoder (adaptive quantisation) from a lossless JPEG XL of the PAM
/// intermediate, then ECT (Huffman/progressive optimisation, metadata stripped) and the policy metadata written back.
/// The <see cref="EncodeKind.Optimize"/> kind runs ECT on the policy-filtered original JPEG (coefficients untouched).
/// </summary>
internal sealed class SjpegAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    internal const string LosslessJxlName = "sjpeg-src.jxl";

    // SSIMULACRA2 → quality, calibrated on photos (q 50 ≈ 66, q 75 ≈ 80, q 85 ≈ 87, q 97 ≈ 93).
    private static readonly (double, double)[] s_quality =
        [(30, 10), (40, 22), (50, 33), (60, 40), (65, 47), (70, 58), (75, 67), (80, 75), (85, 82), (90, 92), (93, 97), (99, 100)];

    public override string Id => "jpeg.sjpeg";

    public override OutputFormat Format => OutputFormat.Jpeg;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Cjxl, ToolId.Djxl, ToolId.Ect];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Optimize];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossy => !a.HasAlpha,
        EncodeKind.Optimize => source == ImageFormat.Jpeg,
        _ => false,
    };

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(s_quality, targetScore));

    /// <remarks>Lossy: the djxl (sjpeg) step. Optimize: the ECT step, which rewrites <see cref="EncodeRequest.OutputPath"/> in place.</remarks>
    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath) => r.Kind == EncodeKind.Optimize
        ? ["-quiet", "-progressive", "-" + Number(EctLevel(r.Effort)), r.OutputPath]
        : [inputPath, r.OutputPath, "--use_sjpeg", "-q", Number((int)Scale.Normalize(r.Parameter ?? 85)), $"--num_threads={Math.Max(1, r.Threads)}", "--quiet"];

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string output = Path.Combine(r.Source.WorkDir, outputName);
        if (r.Kind == EncodeKind.Optimize)
        {
            // The reference already carries exactly the policy metadata (and the original orientation), so ECT keeps it.
            File.Copy(await r.Source.GetJpegReferenceAsync(ct).ConfigureAwait(false), output, overwrite: true);
            await RunAsync(r, ToolId.Ect, BuildArguments(r with { OutputPath = outputName }, outputName), steps, ct).ConfigureAwait(false);
            return;
        }

        // Shared by every quality probe of this source; created once, not cancelled by any single caller.
        string jxl = await r.Source.GetDerivedAsync(LosslessJxlName, async (path, sharedCt) =>
        {
            string pam = Name(await r.Source.GetPamAsync(sharedCt).ConfigureAwait(false));
            await RunAsync(r, ToolId.Cjxl, [pam, Name(path), "-d", "0", "-e", "1", $"--num_threads={Math.Max(1, r.Threads)}", "--quiet"], steps, sharedCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await RunAsync(r, ToolId.Djxl, BuildArguments(r with { OutputPath = outputName }, Name(jxl)), steps, ct).ConfigureAwait(false);
        await RunAsync(r, ToolId.Ect, ["-quiet", "-strip", "-progressive", "-" + Number(EctLevel(r.Effort)), outputName], steps, ct).ConfigureAwait(false);

        ImageMetadata m = r.Source.Metadata;
        if (m.Icc is not null || m.Exif is not null || m.Xmp is not null)
        {
            await RunManagedAsync("set JPEG metadata (ICC/EXIF/XMP)", steps, () =>
                File.WriteAllBytes(output, JpegSegments.Rewrite(File.ReadAllBytes(output), m.Exif, m.Xmp, m.Icc, keepOtherMetadata: false)), ct).ConfigureAwait(false);
        }
    }

    private static int EctLevel(EffortLevel effort) => ByEffort(effort, 1, 3, 6, 9);
}
