namespace Carbonado.Core.Metrics;

/// <summary>A SSIMULACRA2 score with its full-resolution per-pixel error heat-map.</summary>
public sealed class Ssimulacra2Map
{
    internal Ssimulacra2Map(int width, int height, float[] error, double score)
    {
        Width = width;
        Height = height;
        Error = error;
        Score = score;
    }

    /// <summary>Width of the map (the image width).</summary>
    public int Width { get; }

    /// <summary>Height of the map (the image height).</summary>
    public int Height { get; }

    /// <summary>
    /// Row-major error of each pixel in [0, 1] at scale 0 (full resolution): the SSIM error (1 − SSIM′) plus the
    /// edge-difference magnitude (artifacts or lost detail), averaged over the X, Y and B channels and clipped to 1.
    /// 0 means the neighbourhood is unchanged. With transparency, the larger error of the two backgrounds.
    /// </summary>
    public float[] Error { get; }

    /// <summary>The SSIMULACRA2 score of the comparison (identical to <see cref="Ssimulacra2Reference.Score"/>).</summary>
    public double Score { get; }
}
