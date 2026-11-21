using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary><c>png.builtin</c>: Carbonado's own <see cref="PngOptimizer"/> (no external tool), the fallback when oxipng and ECT are missing.</summary>
internal sealed class BuiltinPngAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    public override string Id => "png.builtin";

    public override OutputFormat Format => OutputFormat.Png;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossless, EncodeKind.Optimize];

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossless => true,
        EncodeKind.Optimize => source == ImageFormat.Png,
        _ => false,
    };

    /// <remarks>No process runs: the optimizer is managed code.</remarks>
    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath) => [];

    protected override Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        var options = new PngOptimizeOptions(
            CleanInvisiblePixels: !r.Source.PreserveInvisibleRgb,
            KeepIcc: true,
            KeepExif: true,
            Effort: ByEffort(r.Effort, 1, 2, 3, 3));
        string output = Path.Combine(r.Source.WorkDir, outputName);
        return RunManagedAsync($"PngOptimizer effort={options.Effort}", steps, () => File.WriteAllBytes(output, PngOptimizer.Optimize(r.Source.Pixels, r.Source.Metadata, options)), ct);
    }
}
