namespace Carbonado.Core.Decoding;

/// <summary>How <see cref="IImageLoader.LoadAsync"/> decodes a source file.</summary>
/// <param name="ApplyOrientation">
/// Rotate/mirror the pixels according to the EXIF orientation so they are upright (orientation and the EXIF tag are
/// then reset to 1). When false the pixels are as stored and <c>Metadata.Orientation</c> reports the source value.
/// </param>
/// <param name="FirstFrameOnly">
/// Accept multi-frame sources (animations, multi-page TIFF, multi-size ICO) and decode their first frame, with the
/// total in <c>DecodedImage.FrameCount</c>. When false such sources are rejected with <see cref="NotSupportedException"/>,
/// so a caller that cannot preserve frames never flattens one silently.
/// </param>
public sealed record LoadOptions(bool ApplyOrientation = true, bool FirstFrameOnly = true)
{
    /// <summary>
    /// The default <see cref="MaxPixels"/>: 2^28 pixels (16384 × 16384, libavif's default limit), at most 1 GiB of
    /// 8-bit or 2 GiB of 16-bit RGBA.
    /// </summary>
    public const long DefaultMaxPixels = 1L << 28;

    private readonly long _maxPixels = DefaultMaxPixels;

    /// <summary>
    /// Largest image (width × height, in pixels) accepted. Larger images — or files whose header declares them — are
    /// rejected with <see cref="InvalidDataException"/> before their pixels are allocated. The codecs never accept more
    /// than 2^30 pixels, whatever the value.
    /// </summary>
    public long MaxPixels
    {
        get => _maxPixels;
        init => _maxPixels = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The pixel limit must be positive.");
    }
}
