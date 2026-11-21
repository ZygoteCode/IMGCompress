using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>webp.gif2webp</c>: animated GIF → animated WebP (lossless with -min_size, or lossy), from the original file.
/// gif2webp copies no metadata (its default would copy the GIF's XMP); the policy metadata is written afterwards.
/// </summary>
internal sealed class Gif2WebpAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    public override string Id => "webp.gif2webp";

    public override OutputFormat Format => OutputFormat.WebP;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Gif2webp];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) =>
        source == ImageFormat.Gif && kind is EncodeKind.Lossy or EncodeKind.Lossless;

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(CwebpAdapter.Quality, targetScore));

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string>();
        if (r.Kind == EncodeKind.Lossless)
        {
            args.AddRange(["-min_size", "-m", "6"]);
        }
        else
        {
            args.AddRange(["-lossy", "-q", Number((int)Scale.Normalize(r.Parameter ?? 75)), "-m", "6"]);
        }

        if (r.Threads > 1)
        {
            args.Add("-mt");
        }

        args.AddRange(["-metadata", "none", "-quiet", inputPath, "-o", r.OutputPath]);
        return args;
    }

    protected override bool PreservesAnimation(EncodeRequest r) => r.Source.OriginalFormat == ImageFormat.Gif;

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        // The original frames are encoded, so the prepared pixels (possibly cleaned or resized) would be ignored.
        if (!r.Source.IsAnimated || r.Source.OriginalFormat != ImageFormat.Gif)
        {
            throw new EncodeFailedException("gif2webp encodes animated GIFs; still images use cwebp.");
        }

        string input = Name(await r.Source.GetOriginalCopyAsync(ct).ConfigureAwait(false));
        await RunAsync(r, ToolId.Gif2webp, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
        await CwebpAdapter.ApplyMetadataAsync(r, outputName, steps, ct).ConfigureAwait(false);
    }
}
