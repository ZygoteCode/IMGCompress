namespace Carbonado.Core.Encoders;

public enum EncodeKind
{
    /// <summary>Lossy encode driven by a quality parameter.</summary>
    Lossy,

    /// <summary>Pixel-exact encode (visible pixels identical).</summary>
    Lossless,

    /// <summary>JPEG → JPEG XL transcode keeping the DCT coefficients; the original JPEG is reconstructable bit-exact.</summary>
    JpegTranscode,

    /// <summary>Same-format lossless optimization of the original file (e.g. ECT on JPEG/PNG).</summary>
    Optimize,
}
