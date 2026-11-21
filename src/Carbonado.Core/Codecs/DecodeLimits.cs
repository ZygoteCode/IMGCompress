using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Guards every decoder against memory exhaustion from hostile or damaged headers: a pixel cap, plausibility checks
/// of the declared size against the bytes actually present, and allocation failures reported as invalid data.
/// </summary>
internal static class DecodeLimits
{
    /// <summary>The codecs' absolute ceiling (1 Gpx; a 16-bit RGBA buffer of that size is 8 GiB).</summary>
    public const long CodecMaxPixels = 1L << 30;

    /// <summary>
    /// Deflate's largest possible expansion (258-byte matches coded in ~2 bits): no valid zlib stream inflates to
    /// more than this many bytes per compressed byte.
    /// </summary>
    public const long MaxDeflateRatio = 1032;

    /// <summary>Throws when <paramref name="width"/>×<paramref name="height"/> exceeds <paramref name="maxPixels"/>.</summary>
    public static void CheckPixels(long width, long height, long maxPixels, string format)
    {
        if (width <= 0 || height <= 0 || width * height > Math.Min(maxPixels, CodecMaxPixels))
        {
            throw new InvalidDataException($"{format}: {width}×{height} exceeds the supported size ({Math.Min(maxPixels, CodecMaxPixels):N0} pixels).");
        }
    }

    /// <summary>
    /// Throws when a seekable <paramref name="stream"/> holds fewer than <paramref name="minimumBytes"/> bytes after its
    /// position: the header promises more data than the file contains (truncated or forged).
    /// </summary>
    public static void CheckAvailable(Stream stream, long minimumBytes, string format)
    {
        if (stream.CanSeek && stream.Length - stream.Position < minimumBytes)
        {
            throw new InvalidDataException($"{format}: the file is smaller than its header declares (truncated or damaged).");
        }
    }

    /// <summary>Allocates a pixel buffer, reporting an allocation failure as an image too large to decode.</summary>
    public static PixelBuffer Allocate(int width, int height, SampleDepth depth, bool clear, string format)
    {
        try
        {
            return PixelBuffer.Create(width, height, depth, clear);
        }
        catch (OutOfMemoryException ex)
        {
            throw new InvalidDataException($"{format}: {width}×{height} is too large to decode with the available memory.", ex);
        }
    }
}
