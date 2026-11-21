using Carbonado.Core.Analysis;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Metrics;

namespace Carbonado.Core.Engine;

/// <summary>
/// Decides which encodes the engine tries for an image, from the profile, the analysis and the installed encoders.
/// </summary>
/// <remarks>
/// <para><b>Formats.</b> <see cref="FormatStrategy.Auto"/> races every allowed format that can represent the image;
/// <see cref="FormatStrategy.Fixed"/> uses the first allowed format that can (JPEG cannot carry alpha, animations need
/// an animation-preserving encoder, the encoder must be installed); <see cref="FormatStrategy.KeepSource"/> stays in
/// the source family (PNG/APNG → PNG, JPEG → JPEG, WebP, AVIF, JPEG XL and GIF stay, HEIC → AVIF, every other
/// format → PNG). GIF has no bundled encoder, so a GIF kept in its family gets no candidate (the original is kept).</para>
/// <para><b>Kinds.</b> Lossy modes plan each format's lossy encoder (searched for the target); lossless mode plans
/// JPEG XL lossless, WebP lossless and the PNG optimizers (AVIF lossless only when AVIF itself was asked for: a fixed
/// or kept format). Graphics also get the lossless candidates in lossy modes, as do PNG outputs of a single-format
/// profile and images too small to be scored (under <see cref="Ssimulacra2.MinimumSize"/>, lossy candidates could not
/// be verified). Palette PNG (pngquant) races only for graphics and for mixed content with at most 65 536 colours, or
/// when PNG is the only format.
/// A PNG source is optimized (<see cref="EncodeKind.Optimize"/>) rather than re-encoded.</para>
/// <para><b>JPEG sources.</b> With JPEG XL allowed and <see cref="CompressionProfile.AllowJpegTranscode"/>, the
/// reversible transcode is added (not for <see cref="FormatStrategy.KeepSource"/>); with JPEG allowed, the
/// coefficient-preserving ECT optimization is added. In lossless mode these exact paths replace pixel re-encodes of
/// the decoded JPEG, which are always larger.</para>
/// <para><b>Animations</b> (more than one frame) are never flattened: only GIF → animated WebP (gif2webp), GIF/APNG →
/// JPEG XL lossless (cjxl) and APNG → optimized APNG (oxipng) are planned; animated WebP/AVIF/JPEG XL get none.</para>
/// <para><b>Effort.</b> Insane adds an AVIF chroma variant: 4:4:4 and 4:2:0 race instead of the automatic choice
/// (except for grayscale images, which AVIF encodes as 4:0:0 either way).</para>
/// </remarks>
public static class CandidatePlanner
{
    private const string Gif2WebpId = "webp.gif2webp";
    private const string CjxlId = "jxl.cjxl";
    private const string OxipngId = "png.oxipng";
    private const string PngquantId = "png.pngquant";

    /// <summary>The candidates to try, in profile format order (lossy first within a format), without duplicates.</summary>
    /// <param name="p">The profile.</param>
    /// <param name="a">The analysis of the prepared pixels (after any resize).</param>
    /// <param name="source">The detected source format.</param>
    /// <param name="animated">The source has more than one frame.</param>
    /// <param name="registry">The installed encoders.</param>
    /// <returns>An empty list when nothing can encode the image under this profile (the engine keeps the original).</returns>
    public static IReadOnlyList<CandidatePlan> Plan(CompressionProfile p, ImageAnalysis a, ImageFormat source, bool animated, EncoderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(registry);

        var plans = new List<CandidatePlan>();
        bool singleFormat = p.Strategy != FormatStrategy.Auto;
        foreach (OutputFormat format in TargetFormats(p, source))
        {
            List<CandidatePlan> forFormat = animated
                ? AnimatedPlans(format, a, source, registry)
                : StillPlans(p, a, source, format, singleFormat, registry);
            if (forFormat.Count == 0)
            {
                continue;
            }

            plans.AddRange(forFormat);
            if (p.Strategy == FormatStrategy.Fixed)
            {
                break;
            }
        }

        if (!animated && source == ImageFormat.Jpeg && p.Mode == QualityMode.Lossless
            && plans.Any(c => c.Kind is EncodeKind.JpegTranscode or EncodeKind.Optimize))
        {
            plans.RemoveAll(c => c.Kind == EncodeKind.Lossless);
        }

        return plans.DistinctBy(c => (c.Encoder.Id, c.Kind, c.Chroma)).ToList();
    }

    /// <summary>The output family of <see cref="FormatStrategy.KeepSource"/> for a source format.</summary>
    internal static OutputFormat SourceFamily(ImageFormat source) => source switch
    {
        ImageFormat.Jpeg => OutputFormat.Jpeg,
        ImageFormat.WebP => OutputFormat.WebP,
        ImageFormat.Avif or ImageFormat.Heic => OutputFormat.Avif,
        ImageFormat.Jxl => OutputFormat.Jxl,
        ImageFormat.Gif => OutputFormat.Gif,
        _ => OutputFormat.Png,
    };

    private static IEnumerable<OutputFormat> TargetFormats(CompressionProfile p, ImageFormat source) =>
        p.Strategy == FormatStrategy.KeepSource ? [SourceFamily(source)] : p.Formats.Distinct();

    private static List<CandidatePlan> StillPlans(CompressionProfile p, ImageAnalysis a, ImageFormat source, OutputFormat format, bool singleFormat, EncoderRegistry registry)
    {
        var plans = new List<CandidatePlan>();
        bool scorable = a.Width >= Ssimulacra2.MinimumSize && a.Height >= Ssimulacra2.MinimumSize;
        bool lossyMode = p.Mode != QualityMode.Lossless;

        if (lossyMode && scorable)
        {
            foreach (IEncoderAdapter adapter in Usable(registry, format, EncodeKind.Lossy, a, source))
            {
                if (adapter.Id == PngquantId && !singleFormat && !PaletteCanCompete(a))
                {
                    continue;
                }

                if (format == OutputFormat.Avif && p.Effort == EffortLevel.Insane && !a.IsGrayscale)
                {
                    plans.Add(new CandidatePlan(adapter, EncodeKind.Lossy, ChromaMode.Yuv444));
                    plans.Add(new CandidatePlan(adapter, EncodeKind.Lossy, ChromaMode.Yuv420));
                }
                else
                {
                    plans.Add(new CandidatePlan(adapter, EncodeKind.Lossy, ChromaMode.Auto));
                }
            }
        }

        bool lossless = !lossyMode || !scorable || a.Kind == ContentKind.Graphic || (singleFormat && format == OutputFormat.Png);
        if (lossless && (format != OutputFormat.Avif || singleFormat))
        {
            foreach (IEncoderAdapter adapter in registry.Available.Where(e => e.Format == format && e.Id != Gif2WebpId))
            {
                // A PNG source is optimized; everything else is re-encoded from the pixels.
                if (format == OutputFormat.Png && source == ImageFormat.Png && Can(adapter, EncodeKind.Optimize, a, source))
                {
                    plans.Add(new CandidatePlan(adapter, EncodeKind.Optimize, ChromaMode.Auto));
                }
                else if (Can(adapter, EncodeKind.Lossless, a, source))
                {
                    plans.Add(new CandidatePlan(adapter, EncodeKind.Lossless, ChromaMode.Auto));
                }
            }
        }

        if (source == ImageFormat.Jpeg)
        {
            // Exact paths that keep the JPEG's DCT coefficients (they ignore resizing; the engine drops them then).
            if (format == OutputFormat.Jxl && p.AllowJpegTranscode && p.Strategy != FormatStrategy.KeepSource)
            {
                plans.AddRange(Usable(registry, format, EncodeKind.JpegTranscode, a, source).Select(e => new CandidatePlan(e, EncodeKind.JpegTranscode, ChromaMode.Auto)));
            }

            if (format == OutputFormat.Jpeg)
            {
                plans.AddRange(Usable(registry, format, EncodeKind.Optimize, a, source).Select(e => new CandidatePlan(e, EncodeKind.Optimize, ChromaMode.Auto)));
            }
        }

        return plans;
    }

    // Only encoders that read every frame from the original file, and only lossless (scores cannot be measured on animations).
    private static List<CandidatePlan> AnimatedPlans(OutputFormat format, ImageAnalysis a, ImageFormat source, EncoderRegistry registry)
    {
        (string Id, EncodeKind Kind)? path = (format, source) switch
        {
            (OutputFormat.WebP, ImageFormat.Gif) => (Gif2WebpId, EncodeKind.Lossless),
            (OutputFormat.Jxl, ImageFormat.Gif or ImageFormat.Apng) => (CjxlId, EncodeKind.Lossless),
            (OutputFormat.Png, ImageFormat.Apng) => (OxipngId, EncodeKind.Optimize),
            _ => null,
        };

        return path is { } p && registry.Find(p.Id) is { } adapter && Can(adapter, p.Kind, a, source)
            ? [new CandidatePlan(adapter, p.Kind, ChromaMode.Auto)]
            : [];
    }

    // A 256-colour palette only reaches high scores on synthetic content: graphics, or mixed images with a countable
    // number of colours (smooth images beyond 65 536 colours plateau around SSIMULACRA2 80, measured).
    private static bool PaletteCanCompete(ImageAnalysis a) =>
        a.Kind == ContentKind.Graphic || (a.Kind == ContentKind.Mixed && a.UniqueColors >= 0);

    private static IEnumerable<IEncoderAdapter> Usable(EncoderRegistry registry, OutputFormat format, EncodeKind kind, ImageAnalysis a, ImageFormat source) =>
        registry.For(format, kind).Where(e => e.Id != Gif2WebpId && e.Supports(kind, a, source));

    private static bool Can(IEncoderAdapter adapter, EncodeKind kind, ImageAnalysis a, ImageFormat source) =>
        adapter.Kinds.Contains(kind) && adapter.Supports(kind, a, source);
}
