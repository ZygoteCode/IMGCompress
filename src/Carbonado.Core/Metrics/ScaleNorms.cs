namespace Carbonado.Core.Metrics;

/// <summary>The 1- and 4-norms of the SSIM and edge-difference maps of one scale (libjxl MsssimScale).</summary>
internal sealed class ScaleNorms
{
    /// <summary>Per channel c: [2c] mean of d, [2c+1] (mean of d⁴)^¼.</summary>
    public double[] Ssim { get; } = new double[6];

    /// <summary>Per channel c: [4c] mean artifact, [4c+1] its 4-norm, [4c+2] mean detail lost, [4c+3] its 4-norm.</summary>
    public double[] Edge { get; } = new double[12];

    /// <summary>
    /// Reduces per-strip sums (18 per strip: per channel Σd, Σd⁴, Σartifact, Σartifact⁴, Σlost, Σlost⁴) in strip order.
    /// </summary>
    public static ScaleNorms FromSums(double[] sums, int strips, long pixels)
    {
        Span<double> total = stackalloc double[18];
        for (int s = 0; s < strips; s++)
        {
            for (int k = 0; k < 18; k++)
            {
                total[k] += sums[(s * 18) + k];
            }
        }

        double onePerPixels = 1.0 / pixels;
        var norms = new ScaleNorms();
        for (int c = 0; c < 3; c++)
        {
            ReadOnlySpan<double> t = total.Slice(c * 6, 6);
            norms.Ssim[c * 2] = onePerPixels * t[0];
            norms.Ssim[(c * 2) + 1] = Math.Sqrt(Math.Sqrt(onePerPixels * t[1]));
            norms.Edge[c * 4] = onePerPixels * t[2];
            norms.Edge[(c * 4) + 1] = Math.Sqrt(Math.Sqrt(onePerPixels * t[3]));
            norms.Edge[(c * 4) + 2] = onePerPixels * t[4];
            norms.Edge[(c * 4) + 3] = Math.Sqrt(Math.Sqrt(onePerPixels * t[5]));
        }

        return norms;
    }
}
