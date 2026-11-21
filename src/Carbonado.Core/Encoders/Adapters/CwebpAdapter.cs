using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// <c>webp.cwebp</c>: WebP lossy (sharp YUV, auto filter, lossless alpha) and lossless. The Windows cwebp build reads
/// its input through WIC (no PNM support, only ICC extraction), so it gets an 8-bit metadata-free PNG and the
/// ICC/EXIF/XMP chunks are written into the WebP container afterwards.
/// </summary>
internal sealed class CwebpAdapter(ToolLocator tools, ProcessRunner runner) : ToolAdapter(tools, runner)
{
    // SSIMULACRA2 → quality, calibrated on photos at -m 6 (q 50 ≈ 66, q 75 ≈ 73, q 85 ≈ 81, q 92 ≈ 85); lossy WebP saturates near 88.
    internal static readonly (double, double)[] Quality =
        [(30, 5), (40, 15), (50, 25), (60, 37), (65, 49), (70, 63), (75, 77), (80, 84), (85, 92), (87, 97), (90, 100), (99, 100)];

    public override string Id => "webp.cwebp";

    public override OutputFormat Format => OutputFormat.WebP;

    public override IReadOnlyList<ToolId> RequiredTools { get; } = [ToolId.Cwebp];

    public override IReadOnlyCollection<EncodeKind> Kinds { get; } = [EncodeKind.Lossy, EncodeKind.Lossless];

    public override ParameterScale Scale { get; } = new(0, 100, HigherIsBetter: true, Resolution: 1);

    public override bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => kind switch
    {
        EncodeKind.Lossy => true,
        EncodeKind.Lossless => !a.Has16BitPrecision,
        _ => false,
    };

    public override double InitialGuess(double targetScore, ImageAnalysis a) => Scale.Normalize(Interpolate(Quality, targetScore));

    public override IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath)
    {
        var args = new List<string>();
        if (r.Kind == EncodeKind.Lossless)
        {
            args.AddRange(["-lossless", "-z", Number(ByEffort(r.Effort, 6, 9, 9, 9))]);
        }
        else
        {
            args.AddRange(["-q", Number((int)Scale.Normalize(r.Parameter ?? 75)), "-m", Number(ByEffort(r.Effort, 4, 6, 6, 6)), "-sharp_yuv", "-af"]);
            if (r.Effort >= EffortLevel.Max)
            {
                args.AddRange(["-pass", "10"]);
            }

            if (r.Source.Analysis.HasAlpha)
            {
                args.AddRange(["-alpha_q", "100"]);
            }
        }

        if (r.Source.PreserveInvisibleRgb)
        {
            args.Add("-exact");
        }

        if (r.Threads > 1)
        {
            args.Add("-mt");
        }

        args.AddRange(["-metadata", "none", "-quiet", inputPath, "-o", r.OutputPath]);
        return args;
    }

    protected override async Task ProduceAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        string input = Name(await r.Source.GetBarePng8Async(ct).ConfigureAwait(false));
        await RunAsync(r, ToolId.Cwebp, BuildArguments(r with { OutputPath = outputName }, input), steps, ct).ConfigureAwait(false);
        await ApplyMetadataAsync(r, outputName, steps, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the source's ICC/EXIF/XMP into a produced WebP.</summary>
    internal static Task ApplyMetadataAsync(EncodeRequest r, string outputName, List<string> steps, CancellationToken ct)
    {
        ImageMetadata m = r.Source.Metadata;
        if (m.Icc is null && m.Exif is null && m.Xmp is null)
        {
            return Task.CompletedTask;
        }

        return RunManagedAsync("set WebP metadata (ICC/EXIF/XMP)", steps, () =>
        {
            string path = Path.Combine(r.Source.WorkDir, outputName);
            byte[] updated = WebPMetadataWriter.Apply(File.ReadAllBytes(path), m.Icc, m.Exif, m.Xmp, r.Source.Pixels.Width, r.Source.Pixels.Height);
            File.WriteAllBytes(path, updated);
        }, ct);
    }
}
