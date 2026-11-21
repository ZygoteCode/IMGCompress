using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using ImageMetadata = Carbonado.Core.Imaging.ImageMetadata;
using PixelFormat = System.Windows.Media.PixelFormat;

namespace Carbonado.Core.Decoding;

/// <summary>
/// Decodes JPEG, BMP, GIF, TIFF, ICO, JPEG XR and DDS through Windows Imaging Component (WPF imaging), on the calling
/// thread. Colour profiles are never applied (pixels stay in their encoded space, the profile travels as metadata),
/// except for CMYK, which is converted to RGB (through the embedded CMYK profile when there is one).
/// </summary>
public static class WicDecoder
{
    // Pixels are copied straight into native memory in bands of at most this many bytes.
    private const long BandBytes = 64L << 20;

    /// <summary>Decodes the first frame (the largest image of an ICO) with its ICC profile, EXIF/XMP (JPEG) and orientation.</summary>
    /// <remarks>The orientation is reported in <see cref="ImageMetadata.Orientation"/>, not applied.</remarks>
    /// <exception cref="InvalidDataException">No WIC codec accepts the data, it is corrupt, or it is larger than the codecs' pixel limit.</exception>
    public static DecodedImage Decode(string path) => Decode(path, DecodeLimits.CodecMaxPixels);

    /// <summary><see cref="Decode(string)"/> for images of at most <paramref name="maxPixels"/> pixels (checked from the header, before WIC decodes anything).</summary>
    internal static DecodedImage Decode(string path, long maxPixels)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        // Checked before decoding: WIC would silently fill the missing part of a half-downloaded JPEG.
        bool jpeg = IsJpeg(stream);
        if (jpeg && !JpegSegments.IsComplete(stream))
        {
            throw new InvalidDataException("The JPEG file is truncated (its end-of-image marker is missing).");
        }

        BitmapDecoder decoder;
        BitmapFrame frame;
        try
        {
            (decoder, frame) = Open(stream, maxPixels);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            // WIC parses JPEG metadata (XMP, EXIF…) while opening and rejects files whose metadata it cannot read. The
            // pixels are usually fine: decode them from a copy without metadata segments (metadata is read separately).
            if (!jpeg || SanitizedJpeg(stream) is not { } clean)
            {
                throw new InvalidDataException($"The image could not be decoded: {ex.Message}", ex);
            }

            try
            {
                (decoder, frame) = Open(clean, maxPixels);
            }
            catch (Exception retry) when (IsDecodeFailure(retry))
            {
                clean.Dispose();
                throw new InvalidDataException($"The image could not be decoded: {ex.Message}", ex);
            }

            clean.Dispose();
        }

        ImageFormat format = FormatOf(decoder, stream);
        GifStructure.Geometry? gif = null;
        if (format == ImageFormat.Gif && (gif = GifStructure.Read(stream)) is not { FirstImageComplete: true })
        {
            throw new InvalidDataException("The GIF file is truncated or damaged.");
        }

        var metadata = new ImageMetadata { DpiX = ValidDpi(frame.DpiX), DpiY = ValidDpi(frame.DpiY) };
        if (format == ImageFormat.Jpeg)
        {
            stream.Position = 0;
            var segments = JpegSegments.Read(stream);
            metadata.Exif = segments.Exif;
            metadata.Xmp = segments.Xmp;
            metadata.Icc = segments.Icc;
            metadata.Orientation = segments.Exif is null ? (ushort)1 : ExifReader.ReadOrientation(segments.Exif);
        }
        else
        {
            metadata.Icc = ReadColorContext(frame);
            if (format == ImageFormat.Tiff)
            {
                metadata.Orientation = ReadTiffOrientation(frame);
            }
        }

        bool cmyk = frame.Format == PixelFormats.Cmyk32;
        PixelBuffer pixels;
        try
        {
            pixels = cmyk ? CopyCmyk(frame) : IsHighDepth(frame.Format) ? Copy(frame, PixelFormats.Rgba64, SampleDepth.Sixteen) : Copy(frame, PixelFormats.Bgra32, SampleDepth.Eight);
            if (gif is not null)
            {
                pixels = PlaceOnCanvas(pixels, gif, maxPixels);
            }
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new InvalidDataException($"The image data is corrupt: {ex.Message}", ex);
        }

        // CMYK pixels are RGB now; any other non-RGB/gray profile cannot describe RGB data.
        if (cmyk || !(IccHeader.IsRgb(metadata.Icc) || IccHeader.IsGray(metadata.Icc)))
        {
            metadata.Icc = null;
        }

        return new DecodedImage(pixels, metadata, format, decoder.Frames.Count, SignificantBits(frame.Format));
    }

    private static (BitmapDecoder Decoder, BitmapFrame Frame) Open(Stream stream, long maxPixels)
    {
        const BitmapCreateOptions Options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

        // The size comes from the header (no pixel is decoded without caching); it is checked before the cached
        // decode below allocates the whole frame, so a forged header cannot exhaust memory.
        stream.Position = 0;
        BitmapFrame probe = PickFrame(BitmapDecoder.Create(stream, Options, BitmapCacheOption.None));
        DecodeLimits.CheckPixels(probe.PixelWidth, probe.PixelHeight, maxPixels, "Image");

        stream.Position = 0;
        try
        {
            var decoder = BitmapDecoder.Create(stream, Options, BitmapCacheOption.OnLoad);
            return (decoder, PickFrame(decoder));
        }
        catch (OutOfMemoryException ex)
        {
            throw new InvalidDataException($"Image: {probe.PixelWidth}×{probe.PixelHeight} is too large to decode with the available memory.", ex);
        }

        static BitmapFrame PickFrame(BitmapDecoder decoder) => decoder is IconBitmapDecoder
            ? decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.Format.BitsPerPixel).First()
            : decoder.Frames[0];
    }

    private static bool IsJpeg(Stream stream)
    {
        stream.Position = 0;
        Span<byte> head = stackalloc byte[3];
        return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
    }

    // A copy of a JPEG without APPn metadata and comment segments; null when it cannot be rewritten.
    private static MemoryStream? SanitizedJpeg(Stream stream)
    {
        var bytes = new byte[stream.Length];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        try
        {
            return new MemoryStream(JpegSegments.Rewrite(bytes, null, null, null, keepOtherMetadata: false), writable: false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is NotSupportedException or FormatException or ArgumentException or ExternalException or InvalidOperationException
            or OverflowException or (IOException and not FileNotFoundException and not DirectoryNotFoundException);

    private static double ValidDpi(double dpi) => double.IsFinite(dpi) && dpi > 0 ? dpi : 96;

    private static ImageFormat FormatOf(BitmapDecoder decoder, Stream stream)
    {
        switch (decoder)
        {
            case JpegBitmapDecoder:
                return ImageFormat.Jpeg;
            case BmpBitmapDecoder:
                return ImageFormat.Bmp;
            case GifBitmapDecoder:
                return ImageFormat.Gif;
            case TiffBitmapDecoder:
                return ImageFormat.Tiff;
            case IconBitmapDecoder:
                return ImageFormat.Ico;
            case WmpBitmapDecoder:
                return ImageFormat.Jxr;
            case PngBitmapDecoder:
                return ImageFormat.Png;
            default:
                Span<byte> header = stackalloc byte[FormatSniffer.HeaderLength];
                stream.Position = 0;
                return FormatSniffer.Detect(header[..stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)]);
        }
    }

    private static bool IsHighDepth(PixelFormat f) =>
        f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 || f == PixelFormats.Rgb48 || f == PixelFormats.Gray16
        || f == PixelFormats.Bgr101010 || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float
        || f == PixelFormats.Rgb128Float || f == PixelFormats.Gray32Float;

    private static int SignificantBits(PixelFormat f) => f == PixelFormats.Bgr101010 ? 10 : IsHighDepth(f) ? 16 : 8;

    private static PixelBuffer Copy(BitmapSource frame, PixelFormat target, SampleDepth depth)
    {
        BitmapSource source = frame.Format == target ? frame : new FormatConvertedBitmap(frame, target, null, 0);
        var px = DecodeLimits.Allocate(source.PixelWidth, source.PixelHeight, depth, clear: false, "Image");
        try
        {
            CopyInto(source, px);
            if (depth == SampleDepth.Eight)
            {
                SwapRedBlue(px);
            }

            return px;
        }
        catch
        {
            px.Dispose();
            throw;
        }
    }

    // CMYK → RGB through the embedded CMYK profile when WIC exposes one, else WIC's device conversion.
    private static PixelBuffer CopyCmyk(BitmapFrame frame)
    {
        BitmapSource rgb;
        try
        {
            rgb = frame.ColorContexts is { Count: > 0 } contexts
                ? new ColorConvertedBitmap(frame, contexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32)
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            return Copy(rgb, PixelFormats.Bgra32, SampleDepth.Eight);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or NotSupportedException or FileFormatException)
        {
            return Copy(new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0), PixelFormats.Bgra32, SampleDepth.Eight);
        }
    }

    private static void CopyInto(BitmapSource source, PixelBuffer px)
    {
        int stride = checked((int)px.Stride);
        int band = (int)Math.Clamp(BandBytes / stride, 1, px.Height);
        for (int y = 0; y < px.Height; y += band)
        {
            int rows = Math.Min(band, px.Height - y);
            source.CopyPixels(new Int32Rect(0, y, px.Width, rows), px.Pointer + (nint)(y * px.Stride), checked(rows * stride), stride);
        }
    }

    // BGRA → RGBA in place.
    private static void SwapRedBlue(PixelBuffer px)
    {
        var shuffle = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        for (int y = 0; y < px.Height; y++)
        {
            Span<byte> row = px.RowBytes(y);
            int i = 0;
            ref byte start = ref MemoryMarshal.GetReference(row);
            for (; i + 16 <= row.Length; i += 16)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref start, (nuint)i), shuffle).StoreUnsafe(ref start, (nuint)i);
            }

            for (; i < row.Length; i += 4)
            {
                (row[i], row[i + 2]) = (row[i + 2], row[i]);
            }
        }
    }

    private static byte[]? ReadColorContext(BitmapFrame frame)
    {
        try
        {
            if (frame.ColorContexts is not { Count: > 0 } contexts)
            {
                return null;
            }

            using var profile = contexts[0].OpenProfileStream();
            using var copy = new MemoryStream();
            profile.CopyTo(copy);
            return copy.Length > 0 ? copy.ToArray() : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or ExternalException or InvalidOperationException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static ushort ReadTiffOrientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata m && m.GetQuery("/ifd/{ushort=274}") is ushort o && o is >= 1 and <= 8 ? o : (ushort)1;
        }
        catch (Exception ex) when (ex is NotSupportedException or ExternalException or InvalidOperationException or ArgumentException)
        {
            return 1;
        }
    }

    // A GIF frame smaller than the logical screen is shown at its offset on a transparent canvas.
    private static PixelBuffer PlaceOnCanvas(PixelBuffer pixels, GifStructure.Geometry gif, long maxPixels)
    {
        int screenW = gif.ScreenWidth, screenH = gif.ScreenHeight, left = gif.Left, top = gif.Top;
        if (screenW <= 0 || screenH <= 0 || (screenW == pixels.Width && screenH == pixels.Height && left == 0 && top == 0)
            || screenW < pixels.Width || screenH < pixels.Height)
        {
            return pixels;
        }

        // The logical screen comes from the header alone: capped like any decoded image.
        PixelBuffer canvas;
        try
        {
            DecodeLimits.CheckPixels(screenW, screenH, maxPixels, "GIF");
            canvas = DecodeLimits.Allocate(screenW, screenH, SampleDepth.Eight, clear: true, "GIF");
        }
        catch
        {
            pixels.Dispose();
            throw;
        }

        for (int y = 0; y < pixels.Height && top + y < screenH; y++)
        {
            int count = Math.Min(pixels.Width, screenW - left);
            if (count > 0)
            {
                pixels.Row32(y)[..count].CopyTo(canvas.Row32(top + y)[left..]);
            }
        }

        pixels.Dispose();
        return canvas;
    }
}
