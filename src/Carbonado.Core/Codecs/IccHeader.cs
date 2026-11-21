using System.Text;

namespace Carbonado.Core.Codecs;

/// <summary>Reads the fixed 128-byte header of an ICC profile.</summary>
internal static class IccHeader
{
    /// <summary>
    /// The data colour space signature ("RGB ", "GRAY", "CMYK", "Lab ", …), or null when <paramref name="icc"/> is not
    /// a plausible ICC profile (too short or missing the "acsp" file signature).
    /// </summary>
    public static string? ColorSpace(byte[]? icc)
    {
        if (icc is null || icc.Length < 128 || !icc.AsSpan(36, 4).SequenceEqual("acsp"u8))
        {
            return null;
        }

        return Encoding.ASCII.GetString(icc, 16, 4);
    }

    public static bool IsGray(byte[]? icc) => ColorSpace(icc) == "GRAY";

    public static bool IsRgb(byte[]? icc) => ColorSpace(icc) == "RGB ";
}
