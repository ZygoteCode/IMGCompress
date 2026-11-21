using System.Collections.Frozen;

namespace Carbonado.Core.Imaging;

/// <summary>Static facts about formats: extensions, display names, mappings.</summary>
public static class FormatInfo
{
    /// <summary>File extensions (lower case, with dot) accepted as inputs when expanding folders or drops.</summary>
    public static FrozenSet<string> InputExtensions { get; } = new[]
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".webp",
        ".avif", ".heic", ".heif", ".jxl", ".ico", ".qoi", ".pnm", ".ppm", ".pgm", ".pbm", ".pam", ".psd",
        ".exr", ".hdr", ".jp2", ".j2k", ".dds", ".jxr", ".wdp", ".hdp", ".tga",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default extension (with dot) for an output format.</summary>
    public static string Extension(OutputFormat format) => format switch
    {
        OutputFormat.Jxl => ".jxl",
        OutputFormat.Avif => ".avif",
        OutputFormat.WebP => ".webp",
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Png => ".png",
        OutputFormat.Gif => ".gif",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Short human name ("JPEG XL", "AVIF", …).</summary>
    public static string DisplayName(OutputFormat format) => format switch
    {
        OutputFormat.Jxl => "JPEG XL",
        OutputFormat.Avif => "AVIF",
        OutputFormat.WebP => "WebP",
        OutputFormat.Jpeg => "JPEG",
        OutputFormat.Png => "PNG",
        OutputFormat.Gif => "GIF",
        _ => format.ToString(),
    };

    /// <summary>The output format that re-encodes into the same family as <paramref name="format"/>, if any.</summary>
    public static OutputFormat? FromImageFormat(ImageFormat format) => format switch
    {
        ImageFormat.Png or ImageFormat.Apng => OutputFormat.Png,
        ImageFormat.Jpeg => OutputFormat.Jpeg,
        ImageFormat.WebP => OutputFormat.WebP,
        ImageFormat.Avif => OutputFormat.Avif,
        ImageFormat.Jxl => OutputFormat.Jxl,
        ImageFormat.Gif => OutputFormat.Gif,
        _ => null,
    };

    /// <summary>True when the extension (with or without dot) is a supported input.</summary>
    public static bool IsInputExtension(string extensionOrPath)
    {
        string ext = extensionOrPath.StartsWith('.') ? extensionOrPath : Path.GetExtension(extensionOrPath);
        return ext.Length > 0 && InputExtensions.Contains(ext);
    }
}
