namespace Carbonado.Core.Analysis;

/// <summary>Content facts used to plan encoding.</summary>
/// <param name="UniqueColors">Exact count of distinct RGBA values, or -1 when greater than 65 536.</param>
/// <param name="FitsPalette">True when at most 256 distinct RGBA values exist.</param>
/// <param name="Has16BitPrecision">True for 16-bit buffers whose samples are not all exact multiples of 257.</param>
/// <param name="EdgeDensity">Share of pixels whose luma gradient magnitude exceeds 32 (0..1).</param>
/// <param name="FlatRatio">Share of pixels identical to their left neighbour (0..1).</param>
public sealed record ImageAnalysis(
    int Width,
    int Height,
    AlphaUsage Alpha,
    bool IsGrayscale,
    int UniqueColors,
    bool FitsPalette,
    bool Has16BitPrecision,
    ContentKind Kind,
    double EdgeDensity,
    double FlatRatio)
{
    public bool HasAlpha => Alpha != AlphaUsage.Opaque;

    public long PixelCount => (long)Width * Height;
}
