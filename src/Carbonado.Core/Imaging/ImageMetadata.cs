namespace Carbonado.Core.Imaging;

/// <summary>Metadata carried alongside pixels.</summary>
public sealed class ImageMetadata
{
    /// <summary>Raw ICC profile, or null for sRGB/unknown.</summary>
    public byte[]? Icc { get; set; }

    /// <summary>Raw EXIF payload in TIFF structure (starting with "II*\0" or "MM\0*"), without the "Exif\0\0" prefix.</summary>
    public byte[]? Exif { get; set; }

    /// <summary>Raw XMP packet (UTF-8 XML).</summary>
    public byte[]? Xmp { get; set; }

    /// <summary>
    /// EXIF orientation (1..8) of the *source*. Decoders that apply the rotation to the pixels reset this to 1.
    /// </summary>
    public ushort Orientation { get; set; } = 1;

    public double DpiX { get; set; } = 96;

    public double DpiY { get; set; } = 96;

    public ImageMetadata Clone() => new()
    {
        Icc = Icc?.ToArray(),
        Exif = Exif?.ToArray(),
        Xmp = Xmp?.ToArray(),
        Orientation = Orientation,
        DpiX = DpiX,
        DpiY = DpiY,
    };
}
