using Carbonado.Core.Imaging;

namespace Carbonado.Core.Decoding;

/// <summary>Decodes source images and Carbonado's own outputs into pixels.</summary>
public interface IImageLoader
{
    /// <summary>
    /// Decodes a source file of any supported format (detected from content, never from the extension), with its
    /// metadata, upright unless <see cref="LoadOptions.ApplyOrientation"/> is false.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is empty, unrecognised, corrupt or truncated, or the image (or the size its header declares) exceeds <see cref="LoadOptions.MaxPixels"/>.</exception>
    /// <exception cref="NotSupportedException">A required decoder tool is missing, or the source is multi-frame and <see cref="LoadOptions.FirstFrameOnly"/> is false.</exception>
    Task<DecodedImage> LoadAsync(string path, LoadOptions options, CancellationToken ct);

    /// <summary>
    /// Decodes an encoded output (for verification) exactly as a viewer would, without applying orientation.
    /// The caller owns the returned buffer.
    /// </summary>
    Task<PixelBuffer> DecodeOutputAsync(string path, OutputFormat format, CancellationToken ct);
}
