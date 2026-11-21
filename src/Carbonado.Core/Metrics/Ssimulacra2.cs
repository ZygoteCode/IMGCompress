using Carbonado.Core.Imaging;

namespace Carbonado.Core.Metrics;

/// <summary>
/// SSIMULACRA2 perceptual similarity (Jon Sneyers, Cloudinary; reference implementation in libjxl, BSD-3-Clause):
/// 100 = identical, about 90 = visually lossless at 1:1, 70 = high, 50 = medium and 30 = low quality; it can go
/// negative. For sRGB and untagged images the result matches libjxl's <c>ssimulacra2</c> tool (v0.13.0) to its 8
/// printed decimals: every float operation of its pipeline is reproduced, including the rounding drift of its
/// recursive Gaussian. Images with transparency are scored on a dark (0.1) and a bright (0.9) background, blended on the
/// encoded values, and the worse score is returned.
/// </summary>
public static class Ssimulacra2
{
    /// <summary>Images must be at least this many pixels wide and high.</summary>
    public const int MinimumSize = 8;

    internal const float C2 = 0.0009f;
    internal const float DarkBackground = 0.1f;
    internal const float BrightBackground = 0.9f;
    private const int MaxScales = 6;

    // Weights of the 108 norms (libjxl tools/ssimulacra2.cc, Msssim::Score).
    private static readonly double[] s_weights =
    [
        0.0, 0.0007376606707406586, 0.0, 0.0, 0.0007793481682867309, 0.0, 0.0, 0.0004371155730107379, 0.0,
        1.1041726426657346, 0.00066284834129271, 0.00015231632783718752, 0.0, 0.0016406437456599754, 0.0,
        1.8422455520539298, 11.441172603757666, 0.0, 0.0007989109436015163, 0.000176816438078653, 0.0,
        1.8787594979546387, 10.94906990605142, 0.0, 0.0007289346991508072, 0.9677937080626833, 0.0,
        0.00014003424285435884, 0.9981766977854967, 0.00031949755934435053, 0.0004550992113792063, 0.0, 0.0,
        0.0013648766163243398, 0.0, 0.0, 0.0, 0.0, 0.0, 7.466890328078848, 0.0, 17.445833984131262,
        0.0006235601634041466, 0.0, 0.0, 6.683678146179332, 0.00037724407979611296, 1.027889937768264,
        225.20515300849274, 0.0, 0.0, 19.213238186143016, 0.0011401524586618361, 0.001237755635509985,
        176.39317598450694, 0.0, 0.0, 24.43300999870476, 0.28520802612117757, 0.0004485436923833408, 0.0, 0.0,
        0.0, 34.77906344483772, 44.835625328877896, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
        0.0008680556573291698, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0005313191874358747, 0.0, 0.00016533814161379112, 0.0,
        0.0, 0.0, 0.0, 0.0, 0.0004179171803251336, 0.0017290828234722833, 0.0, 0.0020827005846636437, 0.0, 0.0,
        8.826982764996862, 23.19243343998926, 0.0, 95.1080498811086, 0.9863978034400682, 0.9834382792465353,
        0.0012286405048278493, 171.2667255897307, 0.9807858872435379, 0.0, 0.0, 0.0, 0.0005130064588990679, 0.0,
        0.00010854057858411537,
    ];

    /// <summary>
    /// Scores <paramref name="distorted"/> against <paramref name="reference"/> (same size, at least 8×8, 8 or 16 bit).
    /// <paramref name="icc"/> is the color profile both images share (null = sRGB). Uses O(width) working memory;
    /// to score many candidates against one reference, use <see cref="Ssimulacra2Reference"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The sizes differ or are below <see cref="MinimumSize"/>.</exception>
    public static double Compute(PixelBuffer reference, PixelBuffer distorted, byte[]? icc = null, CancellationToken ct = default)
    {
        Validate(reference, distorted);
        var linearizer = ColorSpace.IccLinearizer.TryCreate(icc);
        if (AlphaOps.IsOpaque(reference) && AlphaOps.IsOpaque(distorted))
        {
            return ComputeOn(reference, distorted, linearizer, background: null, ct);
        }

        return Math.Min(
            ComputeOn(reference, distorted, linearizer, DarkBackground, ct),
            ComputeOn(reference, distorted, linearizer, BrightBackground, ct));
    }

    /// <summary>The score with both images blended on <paramref name="background"/> (null = no blending).</summary>
    internal static double ComputeOn(PixelBuffer reference, PixelBuffer distorted, ColorSpace.IccLinearizer? icc, float? background, CancellationToken ct)
    {
        var decoder = LinearDecoder.Get(icc, background);
        var norms = Ssimulacra2Pass.RunPyramid(
            PassMode.ScoreFused,
            ScaleSizes(reference.Width, reference.Height),
            new PixelLinearSource(reference, decoder),
            new PixelLinearSource(distorted, decoder),
            referencePlanes: null,
            map: null,
            mapTakesMax: false,
            ct);
        return FromNorms(Array.ConvertAll(norms, n => n!));
    }

    /// <summary>
    /// Sizes of the computed scales: halving (rounding up) while the previous scale is at least 8×8, up to 6 scales —
    /// libjxl checks the size before downsampling, so the last scale may be smaller than 8.
    /// </summary>
    internal static List<(int Width, int Height)> ScaleSizes(int width, int height)
    {
        var sizes = new List<(int Width, int Height)> { (width, height) };
        while (sizes.Count < MaxScales && sizes[^1].Width >= MinimumSize && sizes[^1].Height >= MinimumSize)
        {
            sizes.Add(((sizes[^1].Width + 1) / 2, (sizes[^1].Height + 1) / 2));
        }

        return sizes;
    }

    /// <summary>
    /// libjxl Msssim::Score: weighted sum of the norms, then a cubic and a power curve. As in libjxl the weight index
    /// only advances over computed scales, so images with fewer than 6 scales use shifted weights.
    /// </summary>
    internal static double FromNorms(IReadOnlyList<ScaleNorms> scales)
    {
        double ssim = 0.0;
        int i = 0;
        for (int c = 0; c < 3; c++)
        {
            foreach (var scale in scales)
            {
                for (int n = 0; n < 2; n++)
                {
                    ssim += s_weights[i++] * Math.Abs(scale.Ssim[(c * 2) + n]);
                    ssim += s_weights[i++] * Math.Abs(scale.Edge[(c * 4) + n]);
                    ssim += s_weights[i++] * Math.Abs(scale.Edge[(c * 4) + n + 2]);
                }
            }
        }

        ssim *= 0.9562382616834844;
        ssim = (2.326765642916932 * ssim) - (0.020884521182843837 * ssim * ssim) + (6.248496625763138e-05 * ssim * ssim * ssim);
        return ssim > 0 ? 100.0 - (10.0 * Math.Pow(ssim, 0.6276336467831387)) : 100.0;
    }

    internal static void Validate(PixelBuffer reference, PixelBuffer distorted)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(distorted);
        ValidateSize(reference);
        if (distorted.Width != reference.Width || distorted.Height != reference.Height)
        {
            throw new ArgumentException($"Image size mismatch: {distorted.Width}×{distorted.Height} vs {reference.Width}×{reference.Height}.", nameof(distorted));
        }
    }

    internal static void ValidateSize(PixelBuffer image)
    {
        if (image.Width < MinimumSize || image.Height < MinimumSize)
        {
            throw new ArgumentException($"SSIMULACRA2 needs at least {MinimumSize}×{MinimumSize} pixels (got {image.Width}×{image.Height}).", nameof(image));
        }
    }
}
