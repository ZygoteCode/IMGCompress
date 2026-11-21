using System.Buffers.Binary;
using System.Text;
using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Reads image dimensions from file headers without decoding pixels, for the scheduler's memory estimates. Every
/// parser is bounds-checked and returns null for damaged headers (the caller then estimates from the file size).
/// </summary>
internal static class ImageHeader
{
    // Containers whose dimensions sit near the start (ISOBMFF meta boxes, WebP chunks) are scanned up to this far.
    private const int ScanLength = 256 * 1024;

    /// <summary>Header facts: size, whether transparency is possible, and whether samples exceed 8 bits.</summary>
    internal readonly record struct Info(int Width, int Height, bool MayHaveAlpha, bool Deep)
    {
        public long PixelCount => (long)Width * Height;
    }

    /// <summary>The header facts of the image at <paramref name="path"/>, or null when they cannot be read cheaply.</summary>
    public static Info? TryRead(string path)
    {
        try
        {
            ImageFormat format = FormatSniffer.DetectFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Info? info = format switch
            {
                ImageFormat.Png or ImageFormat.Apng => Png(stream),
                ImageFormat.Jpeg => Jpeg(stream),
                ImageFormat.Gif => Gif(Head(stream, 10)),
                ImageFormat.WebP => WebP(Head(stream, ScanLength)),
                ImageFormat.Bmp => Bmp(Head(stream, 30)),
                ImageFormat.Tiff => Tiff(stream),
                ImageFormat.Avif or ImageFormat.Heic => Isobmff(Head(stream, ScanLength), format == ImageFormat.Avif && AvifInfo.BitDepth(path) > 8),
                ImageFormat.Jxl => Jxl(Head(stream, ScanLength)),
                ImageFormat.Qoi => Qoi(Head(stream, 14)),
                ImageFormat.Psd => Psd(Head(stream, 26)),
                ImageFormat.Tga => Tga(Head(stream, 18)),
                ImageFormat.Pnm => Pnm(Head(stream, 4096)),
                ImageFormat.Ico => Ico(Head(stream, 6 + (16 * 256))),
                _ => null,
            };
            return info is { Width: > 0, Height: > 0 } ? info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] Head(Stream s, int length)
    {
        var buffer = new byte[(int)Math.Min(length, s.Length)];
        s.Position = 0;
        int read = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static Info? Png(Stream s)
    {
        byte[] head = Head(s, 33);
        if (head.Length < 33 || !head.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16)));
        int height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20)));
        byte depth = head[24], colorType = head[25];
        bool alpha = colorType is 4 or 6 || HasPngChunkBeforeData(s, "tRNS"u8);
        return new Info(width, height, alpha, depth == 16);
    }

    // Walks chunk headers (seeking over the data) until IDAT.
    private static bool HasPngChunkBeforeData(Stream s, ReadOnlySpan<byte> type)
    {
        Span<byte> header = stackalloc byte[8];
        long position = 8;
        for (int i = 0; i < 1024 && position + 8 <= s.Length; i++)
        {
            s.Position = position;
            if (s.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            {
                return false;
            }

            if (header[4..].SequenceEqual(type))
            {
                return true;
            }

            if (header[4..].SequenceEqual("IDAT"u8))
            {
                return false;
            }

            position += 12L + BinaryPrimitives.ReadUInt32BigEndian(header);
        }

        return false;
    }

    // Walks the marker segments (seeking over them, so large APP segments cost nothing) until a frame header.
    private static Info? Jpeg(Stream s)
    {
        Span<byte> buf = stackalloc byte[8];
        long position = 2;
        for (int i = 0; i < 4096 && position + 4 <= s.Length; i++)
        {
            s.Position = position;
            if (s.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2 || buf[0] != 0xFF)
            {
                return null;
            }

            byte marker = buf[1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null; // end of image or scan data before any frame header
            }

            if (s.ReadAtLeast(buf[..2], 2, throwOnEndOfStream: false) < 2)
            {
                return null;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(buf);
            bool frame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (frame)
            {
                if (s.ReadAtLeast(buf[..5], 5, throwOnEndOfStream: false) < 5)
                {
                    return null;
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(buf[1..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(buf[3..]);
                return new Info(width, height, false, buf[0] > 8);
            }

            position += 2 + length;
        }

        return null;
    }

    private static Info? Gif(byte[] h) =>
        h.Length < 10 ? null : new Info(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8)), true, false);

    private static Info? WebP(byte[] h)
    {
        if (h.Length < 30)
        {
            return null;
        }

        ReadOnlySpan<byte> type = h.AsSpan(12, 4);
        ReadOnlySpan<byte> data = h.AsSpan(20);
        if (type.SequenceEqual("VP8X"u8))
        {
            bool alpha = (data[0] & 0x10) != 0 || (data[0] & 0x02) != 0;
            return new Info(1 + U24(data[4..]), 1 + U24(data[7..]), alpha, false);
        }

        if (type.SequenceEqual("VP8 "u8) && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
        {
            return new Info(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF, false, false);
        }

        if (type.SequenceEqual("VP8L"u8) && data[0] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            return new Info((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, ((bits >> 28) & 1) != 0, false);
        }

        return null;

        static int U24(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16);
    }

    private static Info? Bmp(byte[] h)
    {
        if (h.Length < 26)
        {
            return null;
        }

        uint dib = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14));
        if (dib == 12)
        {
            return new Info(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(18)), BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(20)), false, false);
        }

        if (h.Length < 30)
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(18));
        int height = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(22));
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28));
        return height == int.MinValue ? null : new Info(width, Math.Abs(height), bpp == 32, bpp > 32);
    }

    // IFD0 of a classic TIFF: ImageWidth (256), ImageLength (257), BitsPerSample (258), SamplesPerPixel (277).
    private static Info? Tiff(Stream s)
    {
        byte[] head = Head(s, 8);
        if (head.Length < 8 || head[0] != head[1] || (head[0] != (byte)'I' && head[0] != (byte)'M'))
        {
            return null;
        }

        bool be = head[0] == (byte)'M';
        if (U16(head, 2, be) != 42)
        {
            return null; // BigTIFF and others: estimated from the file size
        }

        long ifd = U32(head, 4, be);
        if (ifd < 8 || ifd + 2 > s.Length)
        {
            return null;
        }

        s.Position = ifd;
        var countBytes = new byte[2];
        if (s.ReadAtLeast(countBytes, 2, throwOnEndOfStream: false) < 2)
        {
            return null;
        }

        int count = Math.Min(U16(countBytes, 0, be), 1024);
        var entries = new byte[count * 12];
        int read = s.ReadAtLeast(entries, entries.Length, throwOnEndOfStream: false);
        int width = 0, height = 0, samples = 1, bits = 8;
        for (int e = 0; e + 12 <= read; e += 12)
        {
            int tag = U16(entries, e, be);
            int type = U16(entries, e + 2, be);
            int value = type == 3 ? U16(entries, e + 8, be) : (int)Math.Min(int.MaxValue, U32(entries, e + 8, be));
            switch (tag)
            {
                case 256: width = value; break;
                case 257: height = value; break;
                case 258 when U32(entries, e + 4, be) == 1: bits = value; break;
                case 277: samples = value; break;
            }
        }

        return new Info(width, height, samples is 2 or >= 4, bits > 8);

        static int U16(byte[] b, int o, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

        static long U32(byte[] b, int o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    }

    // AVIF/HEIF: the largest 'ispe' (image spatial extents) property; alpha when an alpha auxiliary image is declared.
    private static Info? Isobmff(byte[] h, bool deep)
    {
        long bestArea = 0;
        int width = 0, height = 0;
        ReadOnlySpan<byte> span = h;
        for (int i = span.IndexOf("ispe"u8); i >= 4 && i + 16 <= span.Length; i = Next(span, i, "ispe"u8))
        {
            int w = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(span[(i + 8)..]));
            int ht = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(span[(i + 12)..]));
            if ((long)w * ht > bestArea)
            {
                (bestArea, width, height) = ((long)w * ht, w, ht);
            }
        }

        bool alpha = span.IndexOf("auxiliary:alpha"u8) >= 0 || span.IndexOf("auxid:1"u8) >= 0;
        return bestArea > 0 ? new Info(width, height, alpha, deep) : null;

        static int Next(ReadOnlySpan<byte> s, int from, ReadOnlySpan<byte> v)
        {
            int rel = s[(from + 1)..].IndexOf(v);
            return rel < 0 ? -1 : from + 1 + rel;
        }
    }

    // JPEG XL: the SizeHeader right after the codestream signature (bare, or inside a jxlc/jxlp box). Alpha would
    // need the full ImageMetadata, so it is assumed possible.
    private static Info? Jxl(byte[] h)
    {
        int start = -1;
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0x0A)
        {
            start = 0;
        }
        else
        {
            long position = 0;
            for (int i = 0; i < 64 && position + 8 <= h.Length; i++)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan((int)position));
                ReadOnlySpan<byte> type = h.AsSpan((int)position + 4, 4);
                int header = 8;
                if (size == 1 && position + 16 <= h.Length)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan((int)position + 8));
                    header = 16;
                }

                if (type.SequenceEqual("jxlc"u8))
                {
                    start = (int)position + header;
                    break;
                }

                if (type.SequenceEqual("jxlp"u8))
                {
                    start = (int)position + header + 4;
                    break;
                }

                if (size == 0 || size < header)
                {
                    return null;
                }

                position += size;
            }
        }

        if (start < 0 || start + 12 > h.Length || h[start] != 0xFF || h[start + 1] != 0x0A)
        {
            return null;
        }

        var bits = new BitReader(h.AsSpan(start + 2, Math.Min(16, h.Length - start - 2)));
        bool small = bits.Read(1) == 1;
        long ySize = small ? (bits.Read(5) + 1) * 8 : U32Size(ref bits) + 1;
        int ratio = (int)bits.Read(3);
        long xSize = ratio switch
        {
            0 => small ? (bits.Read(5) + 1) * 8 : U32Size(ref bits) + 1,
            1 => ySize,
            2 => ySize * 12 / 10,
            3 => ySize * 4 / 3,
            4 => ySize * 3 / 2,
            5 => ySize * 16 / 9,
            6 => ySize * 5 / 4,
            _ => ySize * 2,
        };
        return xSize is > 0 and <= int.MaxValue && ySize is > 0 and <= int.MaxValue ? new Info((int)xSize, (int)ySize, true, false) : null;

        static long U32Size(ref BitReader b)
        {
            int selector = (int)b.Read(2);
            return b.Read(selector switch { 0 => 9, 1 => 13, 2 => 18, _ => 30 });
        }
    }

    private static Info? Qoi(byte[] h) =>
        h.Length < 14 ? null : new Info((int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4))), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(8))), h[12] == 4, false);

    private static Info? Psd(byte[] h)
    {
        if (h.Length < 26)
        {
            return null;
        }

        int channels = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(12));
        int height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(14)));
        int width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(18)));
        int depth = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(22));
        return new Info(width, height, channels >= 4, depth > 8);
    }

    private static Info? Tga(byte[] h) =>
        h.Length < 18 ? null : new Info(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(14)), h[16] == 32 || (h[17] & 0x0F) != 0, h[16] > 32);

    // Netpbm text headers: "P1".."P6" width height [maxval], or P7 (PAM) keyword lines.
    private static Info? Pnm(byte[] h)
    {
        if (h.Length < 3 || h[0] != (byte)'P')
        {
            return null;
        }

        string text = Encoding.ASCII.GetString(h);
        var tokens = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            string content = line.Split('#')[0];
            tokens.AddRange(content.Split([' ', '\t', '\r', '\v', '\f'], StringSplitOptions.RemoveEmptyEntries));
            if (tokens.Count > 16 || content.Contains("ENDHDR", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (h[1] == (byte)'7')
        {
            int width = Keyword("WIDTH"), height = Keyword("HEIGHT"), depth = Keyword("DEPTH"), max = Keyword("MAXVAL");
            return new Info(width, height, depth is 2 or 4, max > 255);
        }

        if (tokens.Count < 3 || !int.TryParse(tokens[1], out int w) || !int.TryParse(tokens[2], out int ht))
        {
            return null;
        }

        int maxval = tokens.Count > 3 && int.TryParse(tokens[3], out int m) ? m : 255;
        return new Info(w, ht, false, h[1] is not ((byte)'1' or (byte)'4') && maxval > 255);

        int Keyword(string key)
        {
            int index = tokens.IndexOf(key);
            return index >= 0 && index + 1 < tokens.Count && int.TryParse(tokens[index + 1], out int v) ? v : 0;
        }
    }

    // The largest entry of the icon directory (the one the decoder uses); 0 in a size byte means 256.
    private static Info? Ico(byte[] h)
    {
        if (h.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4));
        int width = 0, height = 0;
        for (int i = 0; i < count && 6 + (16 * i) + 16 <= h.Length; i++)
        {
            int w = h[6 + (16 * i)] is 0 ? 256 : h[6 + (16 * i)];
            int ht = h[7 + (16 * i)] is 0 ? 256 : h[7 + (16 * i)];
            if (w * ht > width * height)
            {
                (width, height) = (w, ht);
            }
        }

        return new Info(width, height, true, false);
    }

    /// <summary>LSB-first bit reader (JPEG XL headers).</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public long Read(int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++, _bit++)
            {
                int index = _bit >> 3;
                long b = index < _data.Length ? (_data[index] >> (_bit & 7)) & 1 : 0;
                value |= b << i;
            }

            return value;
        }
    }
}
