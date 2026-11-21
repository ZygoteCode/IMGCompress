using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Decoding;

/// <summary>
/// Decodes any supported image into an upright <see cref="DecodedImage"/>. The format is sniffed from the content
/// (a PNG named .jpg is decoded as PNG); managed decoders handle PNG/APNG and Netpbm, WIC handles
/// JPEG/BMP/GIF/TIFF/ICO/JPEG XR/DDS, and tools handle WebP (dwebp), AVIF (avifdec), JPEG XL (djxl) and the rest (ffmpeg).
/// </summary>
/// <remarks>
/// EXIF orientation is applied for JPEG, TIFF, PNG and WebP. AVIF, JPEG XL and HEIF carry their own transforms,
/// which their decoders always apply; their EXIF orientation tag is reset to 1 so it can never be applied twice.
/// </remarks>
public sealed class ImageLoader : IImageLoader
{
    private readonly ToolDecoders _tools;

    /// <param name="tools">Locates the decoder tools.</param>
    /// <param name="runner">Runs them.</param>
    /// <param name="workDir">Private directory for temporary decode files (created if missing).</param>
    public ImageLoader(ToolLocator tools, ProcessRunner runner, string workDir)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(workDir);
        Directory.CreateDirectory(workDir);
        _tools = new ToolDecoders(tools, runner, workDir);
    }

    /// <inheritdoc/>
    public async Task<DecodedImage> LoadAsync(string path, LoadOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The file does not exist: {path}", path);
        }

        if (file.Length == 0)
        {
            throw new InvalidDataException("The file is empty.");
        }

        ImageFormat format = FormatSniffer.DetectFile(path);
        if (format == ImageFormat.Unknown)
        {
            throw new InvalidDataException("The file is not a recognised image (unknown format, or the data is damaged).");
        }

        int frames = FormatSniffer.CountFrames(path, format);
        RejectMultiFrame(options, format, frames);
        DecodedImage image = await DecodeAsync(path, format, options.MaxPixels, ct).ConfigureAwait(false);
        try
        {
            frames = Math.Max(frames, image.FrameCount);
            RejectMultiFrame(options, format, frames);
            ApplyOrientation(image, format, options.ApplyOrientation);
            return frames == image.FrameCount
                ? image
                : new DecodedImage(image.DetachPixels(), image.Metadata, image.SourceFormat, frames, image.SignificantBits);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<PixelBuffer> DecodeOutputAsync(string path, OutputFormat format, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();
        // Outputs come from Carbonado's own encoders, so only the codecs' absolute ceiling applies.
        const long Limit = DecodeLimits.CodecMaxPixels;
        using DecodedImage image = format switch
        {
            OutputFormat.Jxl => await _tools.JxlAsync(path, withMetadata: false, Limit, ct).ConfigureAwait(false),
            OutputFormat.Avif => await _tools.AvifAsync(path, Limit, ct).ConfigureAwait(false),
            OutputFormat.WebP => await _tools.WebPAsync(path, Limit, ct).ConfigureAwait(false),
            OutputFormat.Png => await Task.Run(() => DecodePng(path, Limit, ct), ct).ConfigureAwait(false),
            OutputFormat.Jpeg or OutputFormat.Gif => await Task.Run(() => WicDecoder.Decode(path, Limit), ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        return image.DetachPixels();
    }

    private Task<DecodedImage> DecodeAsync(string path, ImageFormat format, long maxPixels, CancellationToken ct) => format switch
    {
        ImageFormat.Png or ImageFormat.Apng => Task.Run(() => DecodePng(path, maxPixels, ct), ct),
        ImageFormat.Pnm => Task.Run(() => DecodePnm(path, maxPixels), ct),
        ImageFormat.Jpeg or ImageFormat.Bmp or ImageFormat.Gif or ImageFormat.Tiff or ImageFormat.Ico or ImageFormat.Jxr or ImageFormat.Dds
            => Task.Run(() => WicDecoder.Decode(path, maxPixels), ct),
        ImageFormat.WebP => _tools.WebPAsync(path, maxPixels, ct),
        ImageFormat.Avif => _tools.AvifAsync(path, maxPixels, ct),
        ImageFormat.Jxl => _tools.JxlAsync(path, withMetadata: true, maxPixels, ct),
        _ => _tools.FfmpegAsync(path, format, maxPixels, ct),
    };

    private static DecodedImage DecodePng(string path, long maxPixels, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return PngDecoder.Decode(fs, maxPixels, ct);
    }

    private static DecodedImage DecodePnm(string path, long maxPixels)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return PnmCodec.Decode(fs, maxPixels);
    }

    private static void RejectMultiFrame(LoadOptions options, ImageFormat format, int frames)
    {
        if (!options.FirstFrameOnly && frames > 1)
        {
            throw new NotSupportedException($"The {format} file has {frames} frames; decoding a single frame would flatten it.");
        }
    }

    private static void ApplyOrientation(DecodedImage image, ImageFormat format, bool apply)
    {
        ImageMetadata metadata = image.Metadata;
        ushort orientation = format switch
        {
            ImageFormat.Jpeg or ImageFormat.Tiff => metadata.Orientation,
            ImageFormat.Png or ImageFormat.Apng or ImageFormat.WebP when metadata.Exif is { } exif => ExifReader.ReadOrientation(exif),
            _ => 1,
        };

        if (apply && orientation != 1)
        {
            image.ReplacePixels(Orientation.Apply(image.Pixels, orientation));
            orientation = 1;
        }

        metadata.Orientation = orientation;

        // Upright pixels must not carry a stale EXIF rotation (it would be applied again downstream).
        if (orientation == 1 && metadata.Exif is { } tiff && ExifReader.ReadOrientation(tiff) != 1)
        {
            try
            {
                metadata.Exif = ExifReader.WithOrientation(tiff, 1);
            }
            catch (InvalidDataException)
            {
                metadata.Exif = null;
            }
        }
    }
}
