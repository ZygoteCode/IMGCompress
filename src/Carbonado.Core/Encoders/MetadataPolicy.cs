namespace Carbonado.Core.Encoders;

public enum MetadataPolicy
{
    /// <summary>Remove everything, including the ICC profile (colors assumed sRGB).</summary>
    StripAll,

    /// <summary>Keep only the ICC color profile (default: smallest output with accurate colors).</summary>
    KeepColorProfile,

    /// <summary>Keep ICC, EXIF and XMP but remove GPS location data.</summary>
    KeepAllButGps,

    /// <summary>Keep all metadata.</summary>
    KeepAll,
}
