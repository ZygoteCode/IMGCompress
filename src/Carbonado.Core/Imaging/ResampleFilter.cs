namespace Carbonado.Core.Imaging;

/// <summary>Reconstruction kernels for <see cref="Resampler"/>.</summary>
public enum ResampleFilter
{
    /// <summary>Windowed sinc with 3 lobes: sharpest, with slight ringing at hard edges (the default).</summary>
    Lanczos3,

    /// <summary>Mitchell–Netravali cubic (B = C = 1/3): softer than Lanczos, almost ringing-free.</summary>
    Mitchell,

    /// <summary>Box (area average for integer factors): no ringing, blockier at non-integer factors.</summary>
    Box,
}
