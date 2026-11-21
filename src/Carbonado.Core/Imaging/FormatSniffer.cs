using System.Buffers.Binary;

namespace Carbonado.Core.Imaging;

/// <summary>
/// Detects image formats from file content (magic bytes and container structure). Content always wins over the
/// file name: a PNG named <c>.jpg</c> is a PNG. The extension is only consulted for formats without a signature (TGA).
/// </summary>
public static class FormatSniffer
{
    /// <summary>Bytes of the file start that are enough to recognise every supported signature.</summary>
    public const int HeaderLength = 64;

    // ISOBMFF "ftyp" boxes may list many compatible brands; read at most this much of a large one.
    private const int MaxFtypLength = 1024;

    // Chunk walks over PNG/RIFF/GIF structures stop after this many entries (defends against hostile files).
    private const int MaxStructureEntries = 1 << 16;

    /// <summary>
    /// Detects the format of a file from its first bytes (<see cref="HeaderLength"/> are enough; fewer are allowed).
    /// </summary>
    /// <param name="header">The start of the file.</param>
    /// <param name="extensionHint">The file name or extension (".tga", "tga" or "photo.tga"); used only for TGA.</param>
    /// <returns>The detected format, or <see cref="ImageFormat.Unknown"/>.</returns>
    /// <remarks>
    /// APNG is reported only when the <c>acTL</c> chunk lies within <paramref name="header"/>;
    /// <see cref="DetectFile"/> walks the whole chunk list.
    /// </remarks>
    public static ImageFormat Detect(ReadOnlySpan<byte> header, string? extensionHint = null)
    {
        ImageFormat format = DetectSignature(header);
        if (format == ImageFormat.Png)
        {
            return ScanPngHeader(header) == PngScan.Animated ? ImageFormat.Apng : ImageFormat.Png;
        }

        if (format != ImageFormat.Unknown)
        {
            return format;
        }

        return IsTgaExtension(extensionHint) && LooksLikeTga(header) ? ImageFormat.Tga : ImageFormat.Unknown;
    }

    /// <summary>
    /// Detects the format of the file at <paramref name="path"/> from its content (and, for TGA only, its extension).
    /// Reads the first <see cref="HeaderLength"/> bytes; PNG chunk headers are walked until <c>IDAT</c> to tell APNG apart.
    /// </summary>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    public static ImageFormat DetectFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderLength];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        header = header[..read];

        if (read == HeaderLength && TryGetFtypLength(header, out int ftypLength) && ftypLength > HeaderLength)
        {
            var box = new byte[Math.Min(ftypLength, MaxFtypLength)];
            stream.Position = 0;
            int boxRead = stream.ReadAtLeast(box, box.Length, throwOnEndOfStream: false);
            return Detect(box.AsSpan(0, boxRead), path);
        }

        ImageFormat format = Detect(header, path);
        if (format == ImageFormat.Png && ScanPngHeader(header) == PngScan.Incomplete && FindPngAnimationFrames(stream) > 0)
        {
            return ImageFormat.Apng;
        }

        return format;
    }

    /// <summary>
    /// True when the file holds more than one frame: a GIF with more than one image descriptor, an APNG whose
    /// <c>acTL</c> announces several frames, a WebP with several <c>ANMF</c> frames, or an AVIF/HEIF image sequence.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="f">Its format, as returned by <see cref="DetectFile"/>.</param>
    public static bool IsAnimated(string path, ImageFormat f) => CountFrames(path, f, limit: 2) > 1;

    /// <summary>
    /// Counts frames (at most <paramref name="limit"/>). Formats without animation return 1; image sequences whose
    /// length is not cheaply known (AVIF <c>avis</c>, HEIF <c>msf1</c>) return 2.
    /// </summary>
    internal static int CountFrames(string path, ImageFormat f, int limit = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        switch (f)
        {
            case ImageFormat.Gif:
            {
                using var s = OpenRead(path);
                return Math.Max(1, CountGifImages(s, limit));
            }

            case ImageFormat.Png or ImageFormat.Apng:
            {
                using var s = OpenRead(path);
                return (int)Math.Clamp(FindPngAnimationFrames(s), 1, limit);
            }

            case ImageFormat.WebP:
            {
                using var s = OpenRead(path);
                return Math.Max(1, CountWebPFrames(s, limit));
            }

            case ImageFormat.Avif or ImageFormat.Heic:
            {
                using var s = OpenRead(path);
                Span<byte> box = stackalloc byte[MaxFtypLength];
                int n = s.ReadAtLeast(box, box.Length, throwOnEndOfStream: false);
                return IsImageSequence(box[..n]) ? Math.Min(2, limit) : 1;
            }

            default:
                return 1;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47];

    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> JxlCodestreamMagic => [0xFF, 0x0A];

    private static ReadOnlySpan<byte> JxlContainerMagic => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20];

    private static ReadOnlySpan<byte> Jp2Magic => [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20];

    private static ReadOnlySpan<byte> J2kCodestreamMagic => [0xFF, 0x4F, 0xFF, 0x51];

    private static ReadOnlySpan<byte> JxrMagic => [0x49, 0x49, 0xBC];

    private static ReadOnlySpan<byte> TiffLittleMagic => [0x49, 0x49, 0x2A, 0x00];

    private static ReadOnlySpan<byte> TiffBigMagic => [0x4D, 0x4D, 0x00, 0x2A];

    private static ReadOnlySpan<byte> BigTiffLittleMagic => [0x49, 0x49, 0x2B, 0x00];

    private static ReadOnlySpan<byte> BigTiffBigMagic => [0x4D, 0x4D, 0x00, 0x2B];

    private static ReadOnlySpan<byte> IcoMagic => [0x00, 0x00, 0x01, 0x00];

    private static ReadOnlySpan<byte> ExrMagic => [0x76, 0x2F, 0x31, 0x01];

    private static ImageFormat DetectSignature(ReadOnlySpan<byte> h)
    {
        if (h.Length < 2)
        {
            return ImageFormat.Unknown;
        }

        if (h.StartsWith(PngMagic))
        {
            return ImageFormat.Png;
        }

        if (h.StartsWith(JpegMagic))
        {
            return ImageFormat.Jpeg;
        }

        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
        {
            return ImageFormat.Gif;
        }

        if (h.StartsWith("RIFF"u8) && h.Length >= 12 && h.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        if (h.StartsWith(JxlCodestreamMagic) || h.StartsWith(JxlContainerMagic))
        {
            return ImageFormat.Jxl;
        }

        if (h.StartsWith(Jp2Magic) || h.StartsWith(J2kCodestreamMagic))
        {
            return ImageFormat.Jp2;
        }

        if (TryGetFtypLength(h, out _))
        {
            return DetectFtyp(h);
        }

        // JPEG XR ("II" 0xBC) must be tested before the generic TIFF magic.
        if (h.StartsWith(JxrMagic))
        {
            return ImageFormat.Jxr;
        }

        if (h.StartsWith(TiffLittleMagic) || h.StartsWith(TiffBigMagic) || h.StartsWith(BigTiffLittleMagic) || h.StartsWith(BigTiffBigMagic))
        {
            return ImageFormat.Tiff;
        }

        if (h.StartsWith("BM"u8) && IsBmpHeader(h))
        {
            return ImageFormat.Bmp;
        }

        if (h.StartsWith(IcoMagic) && IsIcoHeader(h))
        {
            return ImageFormat.Ico;
        }

        if (h.StartsWith("qoif"u8))
        {
            return ImageFormat.Qoi;
        }

        if (h.StartsWith("8BPS"u8) && h.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(h[4..]) is 1 or 2)
        {
            return ImageFormat.Psd;
        }

        if (h.StartsWith(ExrMagic))
        {
            return ImageFormat.Exr;
        }

        if (h.StartsWith("#?RADIANCE"u8) || h.StartsWith("#?RGBE"u8))
        {
            return ImageFormat.Hdr;
        }

        if (h.StartsWith("DDS "u8))
        {
            return ImageFormat.Dds;
        }

        // Netpbm: "P1".."P7" followed by whitespace.
        if (h.Length >= 3 && h[0] == (byte)'P' && h[1] is >= (byte)'1' and <= (byte)'7' && h[2] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            return ImageFormat.Pnm;
        }

        return ImageFormat.Unknown;
    }

    private static bool IsBmpHeader(ReadOnlySpan<byte> h) =>
        h.Length >= 18 && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;

    private static bool IsIcoHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) == 0)
        {
            return false;
        }

        // First directory entry: reserved byte 0, colour planes 0 or 1.
        return h.Length < 22 || (h[9] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(h[10..]) <= 1);
    }

    private static bool TryGetFtypLength(ReadOnlySpan<byte> h, out int length)
    {
        length = 0;
        if (h.Length < 16 || !h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(h);
        if (size < 16 || size > int.MaxValue)
        {
            return false;
        }

        length = (int)size;
        return true;
    }

    private static ImageFormat DetectFtyp(ReadOnlySpan<byte> h)
    {
        ReadOnlySpan<byte> major = h.Slice(8, 4);
        if (IsAvifBrand(major))
        {
            return ImageFormat.Avif;
        }

        if (IsHeicBrand(major))
        {
            return ImageFormat.Heic;
        }

        bool heifContainer = major.SequenceEqual("mif1"u8) || major.SequenceEqual("msf1"u8) || major.SequenceEqual("miaf"u8);
        bool heic = false;
        TryGetFtypLength(h, out int boxLength);
        int end = Math.Min(boxLength, h.Length);
        for (int o = 16; o + 4 <= end; o += 4)
        {
            ReadOnlySpan<byte> brand = h.Slice(o, 4);
            if (IsAvifBrand(brand))
            {
                return ImageFormat.Avif;
            }

            heic |= IsHeicBrand(brand);
        }

        return heic || heifContainer ? ImageFormat.Heic : ImageFormat.Unknown;
    }

    private static bool IsAvifBrand(ReadOnlySpan<byte> b) => b.SequenceEqual("avif"u8) || b.SequenceEqual("avis"u8);

    private static bool IsHeicBrand(ReadOnlySpan<byte> b) =>
        b.SequenceEqual("heic"u8) || b.SequenceEqual("heix"u8) || b.SequenceEqual("heim"u8) || b.SequenceEqual("heis"u8)
        || b.SequenceEqual("hevc"u8) || b.SequenceEqual("hevx"u8);

    private static bool IsImageSequence(ReadOnlySpan<byte> h)
    {
        if (!TryGetFtypLength(h, out _))
        {
            return false;
        }

        ReadOnlySpan<byte> major = h.Slice(8, 4);
        return major.SequenceEqual("avis"u8) || major.SequenceEqual("msf1"u8) || major.SequenceEqual("hevc"u8) || major.SequenceEqual("hevx"u8);
    }

    private static bool IsTgaExtension(string? hint)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return false;
        }

        ReadOnlySpan<char> ext = hint.Contains('.') ? Path.GetExtension(hint.AsSpan()) : hint.AsSpan();
        if (ext.StartsWith('.'))
        {
            ext = ext[1..];
        }

        return ext.Equals("tga", StringComparison.OrdinalIgnoreCase) || ext.Equals("icb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("vda", StringComparison.OrdinalIgnoreCase) || ext.Equals("vst", StringComparison.OrdinalIgnoreCase);
    }

    // TGA has no signature: sanity-check the 18-byte header (colour-map type, image type, pixel depth, size).
    private static bool LooksLikeTga(ReadOnlySpan<byte> h)
    {
        if (h.Length < 18)
        {
            return false;
        }

        byte colorMapType = h[1], imageType = h[2], pixelDepth = h[16];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]), height = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
        return colorMapType <= 1 && imageType is 1 or 2 or 3 or 9 or 10 or 11 && pixelDepth is 8 or 15 or 16 or 24 or 32
            && width > 0 && height > 0;
    }

    private enum PngScan
    {
        /// <summary>The header ended before <c>IDAT</c> or <c>acTL</c> was reached.</summary>
        Incomplete,

        /// <summary><c>IDAT</c> was reached without <c>acTL</c>.</summary>
        Still,

        /// <summary><c>acTL</c> precedes <c>IDAT</c>.</summary>
        Animated,
    }

    private static PngScan ScanPngHeader(ReadOnlySpan<byte> h)
    {
        long offset = 8;
        while (offset + 8 <= h.Length)
        {
            int o = (int)offset;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(h[o..]);
            ReadOnlySpan<byte> type = h.Slice(o + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return PngScan.Animated;
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8) || length > int.MaxValue)
            {
                return PngScan.Still;
            }

            offset += 12L + length;
        }

        return PngScan.Incomplete;
    }

    // Returns the acTL frame count when acTL precedes IDAT, otherwise 0. Seeks over chunk payloads.
    private static long FindPngAnimationFrames(Stream s)
    {
        Span<byte> buffer = stackalloc byte[12];
        long offset = 8;
        for (int i = 0; i < MaxStructureEntries; i++)
        {
            s.Position = offset;
            if (s.ReadAtLeast(buffer[..8], 8, throwOnEndOfStream: false) < 8)
            {
                return 0;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            ReadOnlySpan<byte> type = buffer.Slice(4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return length >= 8 && s.ReadAtLeast(buffer[..4], 4, throwOnEndOfStream: false) == 4
                    ? BinaryPrimitives.ReadUInt32BigEndian(buffer)
                    : 0;
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8) || length > int.MaxValue)
            {
                return 0;
            }

            offset += 12L + length;
        }

        return 0;
    }

    private static int CountGifImages(Stream s, int limit)
    {
        Span<byte> buffer = stackalloc byte[13];
        if (s.ReadAtLeast(buffer, 13, throwOnEndOfStream: false) < 13)
        {
            return 0;
        }

        byte screenFlags = buffer[10];
        if ((screenFlags & 0x80) != 0)
        {
            s.Seek(3 << ((screenFlags & 7) + 1), SeekOrigin.Current);
        }

        int images = 0;
        for (int i = 0; i < MaxStructureEntries * 16; i++)
        {
            int introducer = s.ReadByte();
            switch (introducer)
            {
                case 0x21:
                    if (s.ReadByte() < 0 || !SkipGifSubBlocks(s))
                    {
                        return images;
                    }

                    break;

                case 0x2C:
                    if (++images >= limit || s.ReadAtLeast(buffer[..9], 9, throwOnEndOfStream: false) < 9)
                    {
                        return images;
                    }

                    byte imageFlags = buffer[8];
                    if ((imageFlags & 0x80) != 0)
                    {
                        s.Seek(3 << ((imageFlags & 7) + 1), SeekOrigin.Current);
                    }

                    if (s.ReadByte() < 0 || !SkipGifSubBlocks(s))
                    {
                        return images;
                    }

                    break;

                default:
                    // 0x3B trailer, end of stream or corruption.
                    return images;
            }
        }

        return images;
    }

    private static bool SkipGifSubBlocks(Stream s)
    {
        while (true)
        {
            int size = s.ReadByte();
            if (size < 0)
            {
                return false;
            }

            if (size == 0)
            {
                return true;
            }

            s.Seek(size, SeekOrigin.Current);
        }
    }

    private static int CountWebPFrames(Stream s, int limit)
    {
        Span<byte> chunk = stackalloc byte[8];
        long offset = 12;
        int frames = 0;
        for (int i = 0; i < MaxStructureEntries; i++)
        {
            s.Position = offset;
            if (s.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("ANMF"u8) && ++frames >= limit)
            {
                break;
            }

            offset += 8L + size + (size & 1);
        }

        return frames;
    }
}
