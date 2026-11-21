using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>png.pngquant</c>: lossy palette PNG (pngquant quantisation, then oxipng). pngquant colour-manages embedded
/// profiles into sRGB, which would shift the pixels away from the source being scored, so it receives a metadata-free
/// 8-bit PNG and the policy metadata is written into the final file afterwards.
/// </summary>
internal sealed class PngquantAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    private static readonly (double, double)[] s_quality =
        [(30, 25), (40, 35), (50, 45), (60, 55), (70, 65), (75, 72), (80, 80), (85, 88), (90, 94), (95, 98), (99, 100)];

    public override string Id => "png.pngquant";

    public override OutputFormat Format => OutputFormat.Png;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Pngquant, ToolId.Oxipng];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind == EncodeKind.Lossy;

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(s_quality, targetScore));

    /// <remarks>The pngquant step (oxipng and the metadata rewrite follow).</remarks>
    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath) =>
        ["--quality", "0-" + Number((int)Scale.Normalize(r.Parameter ?? 80)), "--speed", Number(ByEffort(r.Effort, 3, 1, 1, 1)), "--strip", "--force", "--output", r.OutputPath, "--", inputPath];

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = await r.Source.GetBarePng8Async(ct).ConfigureAwait(false);
        string quantized = TempName(".png");
        try
        {
            await RunAsync(r, ToolId.Pngquant, BuildArguments(r with { OutputPath = quantized }, Name(input)), steps, ct).ConfigureAwait(false);
            await RunAsync(r, ToolId.Oxipng, ["-o", "2", "--strip", "all", "-t", Number(Math.Max(1, r.Threads)), "-q", "--out", outputName, quantized], steps, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(Path.Combine(r.Source.WorkDir, quantized));
        }

        if (!PngStrip.NothingToKeep(r.Source))
        {
            string output = Path.Combine(r.Source.WorkDir, outputName);
            await RunManagedAsync("set PNG metadata (ICC/DPI/EXIF/XMP)", steps, () =>
                File.WriteAllBytes(output, PngEncoder.ReplaceMetadata(File.ReadAllBytes(output), r.Source.Metadata)), ct).ConfigureAwait(false);
        }
    }
}
