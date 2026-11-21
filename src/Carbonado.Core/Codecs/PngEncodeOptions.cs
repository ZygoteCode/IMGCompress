using System.IO.Compression;

namespace Carbonado.Core.Codecs;

/// <summary>PNG colour types (IHDR byte 9).</summary>
public enum PngColorType : byte
{
    Gray = 0,
    Rgb = 2,
    Palette = 3,
    GrayAlpha = 4,
    Rgba = 6,
}

/// <summary>How the encoder picks the per-row PNG filter.</summary>
public enum PngFilterStrategy
{
    /// <summary>Filter type 0 on every row.</summary>
    None,

    /// <summary>Filter type 1 (left neighbour) on every row.</summary>
    Sub,

    /// <summary>Filter type 2 (pixel above) on every row.</summary>
    Up,

    /// <summary>Filter type 3 (mean of left and above) on every row.</summary>
    Average,

    /// <summary>Filter type 4 (Paeth predictor) on every row.</summary>
    Paeth,

    /// <summary>Per row, the filter with the smallest sum of absolute signed residuals (libpng's heuristic).</summary>
    MinSum,

    /// <summary>Per row, the filter whose residual bytes have the lowest Shannon entropy.</summary>
    Entropy,
}

/// <summary>Options for <see cref="PngEncoder.Encode"/>.</summary>
/// <param name="ColorType">
/// Output colour type; null picks the smallest exact non-palette type (alpha dropped when fully opaque, gray when
/// R = G = B and the ICC profile, if any, is a gray profile). An explicit type must represent every pixel exactly.
/// </param>
/// <param name="BitDepth">Bits per sample (1, 2, 4, 8, 16 as legal for the type); null keeps the buffer depth (palette: 8).</param>
/// <param name="Level">Deflate effort.</param>
/// <param name="Filter">Row filter strategy.</param>
/// <param name="WriteIcc">Write the ICC profile (iCCP) when present and compatible with the colour type.</param>
/// <param name="WriteExif">Write EXIF (eXIf) and XMP (iTXt) when present.</param>
/// <param name="Interlace">Adam7 interlacing.</param>
public sealed record PngEncodeOptions(
    PngColorType? ColorType = null,
    int? BitDepth = null,
    CompressionLevel Level = CompressionLevel.SmallestSize,
    PngFilterStrategy Filter = PngFilterStrategy.MinSum,
    bool WriteIcc = true,
    bool WriteExif = false,
    bool Interlace = false);
