namespace Carbonado.Core.Imaging;

/// <summary>An upright decoded image (orientation applied) with its metadata and source facts. Owns <see cref="Pixels"/>.</summary>
public sealed class DecodedImage : IDisposable
{
    public DecodedImage(PixelBuffer pixels, ImageMetadata metadata, ImageFormat sourceFormat, int frameCount = 1, int significantBits = 8)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(metadata);
        Pixels = pixels;
        Metadata = metadata;
        SourceFormat = sourceFormat;
        FrameCount = Math.Max(1, frameCount);
        SignificantBits = significantBits;
    }

    public PixelBuffer Pixels { get; private set; }

    public ImageMetadata Metadata { get; }

    public ImageFormat SourceFormat { get; }

    /// <summary>Number of frames in the source (> 1 means animated; only the first frame is in <see cref="Pixels"/>).</summary>
    public int FrameCount { get; }

    /// <summary>Significant bits per channel in the source (8, 10, 12, 16…).</summary>
    public int SignificantBits { get; }

    public bool IsAnimated => FrameCount > 1;

    /// <summary>Replaces the pixel buffer (disposing the previous one), e.g. after orientation or resize.</summary>
    public void ReplacePixels(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (!ReferenceEquals(pixels, Pixels))
        {
            Pixels.Dispose();
            Pixels = pixels;
        }
    }

    /// <summary>Takes ownership of the pixels away from this instance (caller must dispose them).</summary>
    public PixelBuffer DetachPixels()
    {
        var p = Pixels;
        Pixels = null!;
        return p;
    }

    public void Dispose() => Pixels?.Dispose();
}
