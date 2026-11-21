using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Netpbm codec. Decodes P1–P7 (ASCII and binary PBM/PGM/PPM, and PAM with GRAYSCALE[_ALPHA]/GRAY[_ALPHA]/RGB[_ALPHA]
/// tuples) at any maxval up to 65535; writes PAM (the lossless intermediate fed to encoders) and PPM.
/// </summary>
public static class PnmCodec
{
    /// <summary>Largest image (in pixels) accepted.</summary>
    /// <remarks>On seekable streams the raster the header declares must also be present in full before it is allocated.</remarks>
    public const long MaxPixels = PngDecoder.MaxPixels;

    /// <summary>Decodes the first image of a Netpbm stream.</summary>
    /// <remarks>Maxvals other than 255 and 65535 are rescaled to the full 8- (≤ 255) or 16-bit range, rounding to nearest.</remarks>
    /// <exception cref="InvalidDataException">Malformed header, unsupported tuple layout, truncated raster, or larger than <see cref="MaxPixels"/>.</exception>
    public static DecodedImage Decode(Stream input) => Decode(input, MaxPixels);

    /// <summary>Decodes the first image of a Netpbm stream of at most <paramref name="maxPixels"/> pixels.</summary>
    internal static DecodedImage Decode(Stream input, long maxPixels)
    {
        ArgumentNullException.ThrowIfNull(input);
        Stream s = input is FileStream or MemoryStream or BufferedStream ? input : new BufferedStream(input, 64 * 1024);
        int p = s.ReadByte(), kind = s.ReadByte();
        if (p != 'P' || kind is < '1' or > '7')
        {
            throw new InvalidDataException("Not a Netpbm file (expected P1–P7).");
        }

        var header = kind == '7' ? ReadPamHeader(s) : ReadClassicHeader(s, kind - '0');
        DecodeLimits.CheckPixels(header.Width, header.Height, maxPixels, "PNM");
        if ((long)header.Width * header.Channels * 2 > Array.MaxLength || (long)header.Width * 8 > int.MaxValue)
        {
            throw new InvalidDataException($"PNM: rows of {header.Width} pixels are too wide.");
        }

        // A forged header must not allocate more than the file holds.
        DecodeLimits.CheckAvailable(s, MinimumRasterBytes(header), "PNM");
        var px = DecodeLimits.Allocate(header.Width, header.Height, header.MaxValue > 255 ? SampleDepth.Sixteen : SampleDepth.Eight, clear: false, "PNM");
        try
        {
            switch (header.Kind)
            {
                case 1:
                    ReadAsciiBitmap(s, px);
                    break;
                case 2 or 3:
                    ReadAscii(s, px, header);
                    break;
                case 4:
                    ReadBinaryBitmap(s, px);
                    break;
                default:
                    ReadBinary(s, px, header);
                    break;
            }

            int bits = header.Kind is 1 or 4 ? 1 : 32 - int.LeadingZeroCount(header.MaxValue);
            return new DecodedImage(px, new ImageMetadata(), ImageFormat.Pnm, 1, bits);
        }
        catch
        {
            px.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a PAM (P7) at the buffer's depth (MAXVAL 255 or 65535). The alpha channel is omitted when every pixel is
    /// opaque (TUPLTYPE RGB), so encoders do not store a useless alpha plane.
    /// </summary>
    public static void WritePam(Stream output, PixelBuffer pixels) => WritePam(output, pixels, allowGray: false);

    /// <summary>
    /// <see cref="WritePam(Stream, PixelBuffer)"/>, optionally storing gray images (R = G = B) as GRAYSCALE[_ALPHA]
    /// (only valid when no RGB colour profile accompanies the data).
    /// </summary>
    internal static void WritePam(Stream output, PixelBuffer pixels, bool allowGray)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pixels);
        var stats = PngPixelStats.Collect(pixels, collectColors: false);
        bool gray = allowGray && stats.IsGray, alpha = !stats.IsOpaque;
        int channels = (gray ? 1 : 3) + (alpha ? 1 : 0);
        string tuple = (gray ? "GRAYSCALE" : "RGB") + (alpha ? "_ALPHA" : string.Empty);
        int maxValue = pixels.Depth == SampleDepth.Eight ? 255 : 65535;
        WriteAscii(output, string.Create(CultureInfo.InvariantCulture,
            $"P7\nWIDTH {pixels.Width}\nHEIGHT {pixels.Height}\nDEPTH {channels}\nMAXVAL {maxValue}\nTUPLTYPE {tuple}\nENDHDR\n"));
        WriteRaster(output, pixels, gray, alpha);
    }

    /// <summary>Writes a binary PPM (P6) at the buffer's depth; alpha is dropped.</summary>
    public static void WritePpm(Stream output, PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pixels);
        int maxValue = pixels.Depth == SampleDepth.Eight ? 255 : 65535;
        WriteAscii(output, string.Create(CultureInfo.InvariantCulture, $"P6\n{pixels.Width} {pixels.Height}\n{maxValue}\n"));
        WriteRaster(output, pixels, gray: false, alpha: false);
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    private static void WriteRaster(Stream output, PixelBuffer px, bool gray, bool alpha)
    {
        int channels = (gray ? 1 : 3) + (alpha ? 1 : 0);
        bool wide = px.Depth == SampleDepth.Sixteen;
        int rowBytes = px.Width * channels * (wide ? 2 : 1);
        if (!wide && channels == 4)
        {
            for (int y = 0; y < px.Height; y++)
            {
                output.Write(px.RowBytes(y));
            }

            return;
        }

        byte[] row = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (int y = 0; y < px.Height; y++)
            {
                Span<byte> dst = row.AsSpan(0, rowBytes);
                if (wide)
                {
                    int o = 0;
                    foreach (Rgba64 c in px.Row64(y))
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(dst[o..], c.R);
                        o += 2;
                        if (!gray)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(dst[o..], c.G);
                            BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 2)..], c.B);
                            o += 4;
                        }

                        if (alpha)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(dst[o..], c.A);
                            o += 2;
                        }
                    }
                }
                else
                {
                    int o = 0;
                    foreach (Rgba32 c in px.Row32(y))
                    {
                        dst[o++] = c.R;
                        if (!gray)
                        {
                            dst[o++] = c.G;
                            dst[o++] = c.B;
                        }

                        if (alpha)
                        {
                            dst[o++] = c.A;
                        }
                    }
                }

                output.Write(dst);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    private sealed record Header(int Kind, int Width, int Height, int MaxValue, int Channels);

    // The smallest raster the header allows: binary rasters exactly; ASCII samples take at least one digit plus a
    // separator (PBM digits need no separator).
    private static long MinimumRasterBytes(Header h)
    {
        long pixels = (long)h.Width * h.Height;
        return h.Kind switch
        {
            1 => pixels,
            2 or 3 => (pixels * h.Channels * 2) - 1,
            4 => (h.Width + 7L) / 8 * h.Height,
            _ => pixels * h.Channels * (h.MaxValue > 255 ? 2 : 1),
        };
    }

    private static Header ReadClassicHeader(Stream s, int kind)
    {
        int width = ReadHeaderInt(s, out _), height = ReadHeaderInt(s, out int terminator);
        int maxValue = 1;
        if (kind is not (1 or 4))
        {
            maxValue = ReadHeaderInt(s, out terminator);
        }

        // Exactly one whitespace byte separates the header from a binary raster (ASCII rasters are free-form).
        if (kind >= 4 && terminator is not (' ' or '\t' or '\n' or '\r' or '\v' or '\f'))
        {
            throw new InvalidDataException("PNM: malformed header.");
        }

        int channels = kind is 3 or 6 ? 3 : 1;
        return Validate(new Header(kind, width, height, maxValue, channels));
    }

    private static Header ReadPamHeader(Stream s)
    {
        if (s.ReadByte() is not ('\n' or '\r' or ' ' or '\t'))
        {
            throw new InvalidDataException("PAM: malformed header.");
        }

        int width = -1, height = -1, depth = -1, maxValue = -1;
        var line = new StringBuilder();
        for (int lines = 0; lines < 1024; lines++)
        {
            line.Clear();
            int c;
            while ((c = s.ReadByte()) >= 0 && c != '\n')
            {
                if (line.Length > 1024)
                {
                    throw new InvalidDataException("PAM: header line too long.");
                }

                line.Append((char)c);
            }

            if (c < 0)
            {
                throw new InvalidDataException("PAM: the header is truncated.");
            }

            string text = line.ToString().Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            if (text == "ENDHDR")
            {
                return Validate(new Header(7, width, height, maxValue, depth));
            }

            int space = text.IndexOfAny([' ', '\t']);
            string key = space < 0 ? text : text[..space];
            string value = space < 0 ? string.Empty : text[(space + 1)..].Trim();
            switch (key)
            {
                case "WIDTH":
                    width = ParseInt(value);
                    break;
                case "HEIGHT":
                    height = ParseInt(value);
                    break;
                case "DEPTH":
                    depth = ParseInt(value);
                    break;
                case "MAXVAL":
                    maxValue = ParseInt(value);
                    break;
                case "TUPLTYPE":
                    // The channel count (DEPTH) defines the layout; known tuple names only confirm it.
                    break;
                default:
                    throw new InvalidDataException($"PAM: unknown header field '{key}'.");
            }
        }

        throw new InvalidDataException("PAM: ENDHDR not found.");
    }

    private static Header Validate(Header h)
    {
        if (h.Width < 1 || h.Height < 1)
        {
            throw new InvalidDataException($"PNM: invalid size {h.Width}×{h.Height}.");
        }

        if (h.MaxValue is < 1 or > 65535)
        {
            throw new InvalidDataException($"PNM: invalid maxval {h.MaxValue}.");
        }

        if (h.Channels is < 1 or > 4)
        {
            throw new InvalidDataException($"PAM: unsupported depth {h.Channels} (1–4 channels are supported).");
        }

        return h;
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : throw new InvalidDataException($"PNM: invalid number '{text}'.");

    // Reads one header integer, skipping whitespace and '#' comments; the byte that ended it is consumed.
    private static int ReadHeaderInt(Stream s, out int terminator)
    {
        int c = SkipWhitespaceAndComments(s);
        if (c is < '0' or > '9')
        {
            throw new InvalidDataException("PNM: malformed header.");
        }

        long value = 0;
        while (c is >= '0' and <= '9')
        {
            value = (value * 10) + (c - '0');
            if (value > int.MaxValue)
            {
                throw new InvalidDataException("PNM: header value out of range.");
            }

            c = s.ReadByte();
        }

        if (c == '#')
        {
            SkipLine(s);
            c = '\n';
        }

        terminator = c;
        return (int)value;
    }

    private static int SkipWhitespaceAndComments(Stream s)
    {
        while (true)
        {
            int c = s.ReadByte();
            if (c == '#')
            {
                SkipLine(s);
                continue;
            }

            if (c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f')
            {
                continue;
            }

            return c;
        }
    }

    private static void SkipLine(Stream s)
    {
        int c;
        while ((c = s.ReadByte()) >= 0 && c != '\n' && c != '\r')
        {
        }
    }

    private static void ReadBinary(Stream s, PixelBuffer px, Header h)
    {
        int bytesPerSample = h.MaxValue > 255 ? 2 : 1;
        int rowBytes = checked(px.Width * h.Channels * bytesPerSample);
        byte[] row = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (int y = 0; y < px.Height; y++)
            {
                Span<byte> raw = row.AsSpan(0, rowBytes);
                if (s.ReadAtLeast(raw, rowBytes, throwOnEndOfStream: false) < rowBytes)
                {
                    throw new InvalidDataException("PNM: the raster is truncated.");
                }

                if (h.MaxValue == 255)
                {
                    StoreRow8(raw, px.Row32(y), h.Channels);
                }
                else if (h.MaxValue == 65535)
                {
                    StoreRow16(raw, px.Row64(y), h.Channels);
                }
                else
                {
                    StoreRowScaled(raw, px, y, h, bytesPerSample);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    private static void StoreRow8(ReadOnlySpan<byte> raw, Span<Rgba32> dst, int channels)
    {
        switch (channels)
        {
            case 4:
                raw.CopyTo(MemoryMarshal.AsBytes(dst));
                break;
            case 3:
                for (int x = 0, o = 0; x < dst.Length; x++, o += 3)
                {
                    dst[x] = new Rgba32(raw[o], raw[o + 1], raw[o + 2]);
                }

                break;
            case 2:
                for (int x = 0, o = 0; x < dst.Length; x++, o += 2)
                {
                    dst[x] = new Rgba32(raw[o], raw[o], raw[o], raw[o + 1]);
                }

                break;
            default:
                for (int x = 0; x < dst.Length; x++)
                {
                    dst[x] = new Rgba32(raw[x], raw[x], raw[x]);
                }

                break;
        }
    }

    private static void StoreRow16(ReadOnlySpan<byte> raw, Span<Rgba64> dst, int channels)
    {
        if (channels == 4)
        {
            BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(raw), MemoryMarshal.Cast<Rgba64, ushort>(dst));
            return;
        }

        for (int x = 0, o = 0; x < dst.Length; x++, o += channels * 2)
        {
            ushort v = Be(raw, o);
            dst[x] = channels switch
            {
                3 => new Rgba64(v, Be(raw, o + 2), Be(raw, o + 4)),
                2 => new Rgba64(v, v, v, Be(raw, o + 2)),
                _ => new Rgba64(v, v, v),
            };
        }
    }

    private static void StoreRowScaled(ReadOnlySpan<byte> raw, PixelBuffer px, int y, Header h, int bytesPerSample)
    {
        Span<int> sample = stackalloc int[4];
        for (int x = 0, o = 0; x < px.Width; x++)
        {
            for (int c = 0; c < h.Channels; c++, o += bytesPerSample)
            {
                sample[c] = bytesPerSample == 1 ? raw[o] : Be(raw, o);
            }

            Store(px, x, y, h, sample);
        }
    }

    private static ushort Be(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt16BigEndian(raw[offset..]);

    private static void ReadAscii(Stream s, PixelBuffer px, Header h)
    {
        Span<int> sample = stackalloc int[4];
        for (int y = 0; y < px.Height; y++)
        {
            for (int x = 0; x < px.Width; x++)
            {
                for (int c = 0; c < h.Channels; c++)
                {
                    sample[c] = ReadAsciiInt(s);
                }

                Store(px, x, y, h, sample);
            }
        }
    }

    private static int ReadAsciiInt(Stream s)
    {
        int c = SkipWhitespaceAndComments(s);
        if (c < 0)
        {
            throw new InvalidDataException("PNM: the raster is truncated.");
        }

        if (c is < '0' or > '9')
        {
            throw new InvalidDataException("PNM: invalid ASCII sample.");
        }

        int value = 0;
        while (c is >= '0' and <= '9')
        {
            value = Math.Min((value * 10) + (c - '0'), 1 << 20);
            c = s.ReadByte();
        }

        if (c == '#')
        {
            SkipLine(s);
        }

        return value;
    }

    // PBM: 1 = black.
    private static void ReadAsciiBitmap(Stream s, PixelBuffer px)
    {
        for (int y = 0; y < px.Height; y++)
        {
            Span<Rgba32> row = px.Row32(y);
            for (int x = 0; x < px.Width; x++)
            {
                int c = SkipWhitespaceAndComments(s);
                if (c is not ('0' or '1'))
                {
                    throw c < 0 ? new InvalidDataException("PBM: the raster is truncated.") : new InvalidDataException("PBM: invalid sample.");
                }

                byte v = c == '1' ? (byte)0 : (byte)255;
                row[x] = new Rgba32(v, v, v);
            }
        }
    }

    private static void ReadBinaryBitmap(Stream s, PixelBuffer px)
    {
        int rowBytes = (px.Width + 7) / 8;
        byte[] raw = new byte[rowBytes];
        for (int y = 0; y < px.Height; y++)
        {
            if (s.ReadAtLeast(raw, rowBytes, throwOnEndOfStream: false) < rowBytes)
            {
                throw new InvalidDataException("PBM: the raster is truncated.");
            }

            Span<Rgba32> row = px.Row32(y);
            for (int x = 0; x < px.Width; x++)
            {
                byte v = ((raw[x >> 3] >> (7 - (x & 7))) & 1) != 0 ? (byte)0 : (byte)255;
                row[x] = new Rgba32(v, v, v);
            }
        }
    }

    // Stores one pixel from raw samples (1 = gray, 2 = gray + alpha, 3 = RGB, 4 = RGBA), rescaling odd maxvals.
    private static void Store(PixelBuffer px, int x, int y, Header h, ReadOnlySpan<int> sample)
    {
        int max = h.MaxValue;
        if (px.Depth == SampleDepth.Eight)
        {
            byte r = Scale8(sample[0], max), a = 255, g = r, b = r;
            if (h.Channels >= 3)
            {
                g = Scale8(sample[1], max);
                b = Scale8(sample[2], max);
            }

            if (h.Channels is 2 or 4)
            {
                a = Scale8(sample[h.Channels - 1], max);
            }

            px.Row32(y)[x] = new Rgba32(r, g, b, a);
        }
        else
        {
            ushort r = Scale16(sample[0], max), a = ushort.MaxValue, g = r, b = r;
            if (h.Channels >= 3)
            {
                g = Scale16(sample[1], max);
                b = Scale16(sample[2], max);
            }

            if (h.Channels is 2 or 4)
            {
                a = Scale16(sample[h.Channels - 1], max);
            }

            px.Row64(y)[x] = new Rgba64(r, g, b, a);
        }
    }

    private static byte Scale8(int v, int max) =>
        max == 255 ? (byte)Math.Min(v, 255) : (byte)(((Math.Min(v, max) * 255) + (max / 2)) / max);

    private static ushort Scale16(int v, int max) =>
        max == 65535 ? (ushort)Math.Min(v, 65535) : (ushort)((((long)Math.Min(v, max) * 65535) + (max / 2)) / max);
}
