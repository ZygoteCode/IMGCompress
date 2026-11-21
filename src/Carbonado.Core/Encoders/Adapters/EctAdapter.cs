using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>png.ect</c>: lossless PNG optimisation with ECT (strongest deflate). ECT rewrites files in place, so it always
/// works on a copy of the PNG intermediate inside the work directory (it also cannot open non-ASCII paths).
/// </summary>
internal sealed class EctAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    public override string Id => "png.ect";

    public override OutputFormat Format => OutputFormat.Png;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Ect];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossless, EncodeKind.Optimize];

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossless => true,
        EncodeKind.Optimize => source == ImageFormat.Png,
        _ => false,
    };

    /// <remarks>ECT optimises <see cref="EncodeRequest.OutputPath"/> in place; <paramref name="inputPath"/> is copied there first.</remarks>
    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string> { "-quiet", "-" + Number(ByEffort(r.Effort, 3, 5, 9, 9)) };
        if (PngStrip.NothingToKeep(r.Source))
        {
            args.Add("-strip");
        }

        if (r.Source.PreserveInvisibleRgb)
        {
            args.Add("--strict");
        }

        if (r.Effort == EffortLevel.Insane)
        {
            args.Add("--allfilters-b");
        }

        args.Add(r.OutputPath);
        return args;
    }

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = await r.Source.GetPngAsync(ct).ConfigureAwait(false);
        File.Copy(input, Path.Combine(r.Source.WorkDir, outputName), overwrite: true);
        await RunAsync(r, ToolId.Ect, BuildArguments(r with { OutputPath = outputName }, Name(input)), steps, ct).ConfigureAwait(false);
    }
}
