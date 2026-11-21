using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Managed PNG/APNG decoder: colour types 0/2/3/4/6 at every legal depth, PLTE + tRNS, Adam7, iCCP, eXIf, XMP (iTXt),
/// pHYs, sBIT, and the APNG default image (first frame) with its frame count. Image data streams through inflate
/// row by row; every chunk that is used is CRC-checked. On seekable streams, chunk lengths and the raster size are
/// checked against the bytes present before anything is allocated, so forged headers cannot exhaust memory.
/// </summary>
/// <remarks>
/// Output is 16-bit only for 16-bit sources. EXIF is returned raw: <see cref="ImageMetadata.Orientation"/> is left at 1
/// (the loader interprets orientation, which differs per container).
/// </remarks>
public static class PngDecoder
{
    /// <summary>Largest image (in pixels) accepted; protects against decompression bombs.</summary>
    /// <remarks>
    /// On seekable streams the raster size is also checked against the bytes present (deflate expands at most
    /// 1032:1), so a forged header cannot allocate more than the file can actually fill.
    /// </remarks>
    public const long MaxPixels = DecodeLimits.CodecMaxPixels;

    // Metadata chunks larger than this are skipped rather than parsed.
    private const int MaxMetadataBytes = 64 << 20;

    /// <summary>Decodes a PNG or APNG (first frame).</summary>
    /// <exception cref="InvalidDataException">The data is not a valid PNG, is corrupt (bad CRC, bad deflate data), truncated, or larger than <see cref="MaxPixels"/>.</exception>
    public static DecodedImage Decode(Stream input) => Decode(input, MaxPixels, CancellationToken.None);

    /// <summary>Decodes a PNG, checking <paramref name="cancellationToken"/> between rows.</summary>
    internal static DecodedImage Decode(Stream input, CancellationToken cancellationToken) => Decode(input, MaxPixels, cancellationToken);

    /// <summary>Decodes a PNG of at most <paramref name="maxPixels"/> pixels, checking <paramref name="cancellationToken"/> between rows.</summary>
    internal static DecodedImage Decode(Stream input, long maxPixels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reader = new ChunkReader(input);
        var header = ReadHeader(reader);
        DecodeLimits.CheckPixels(header.Width, header.Height, maxPixels, "PNG");
        var state = new DecodeState(header);
        PixelBuffer? pixels = null;
        try
        {
            while (true)
            {
                if (!reader.TryReadHeader(out uint length, out uint type))
                {
                    // Every row is decoded and CRC-checked; only trailing chunks/IEND are missing: tolerated.
                    if (pixels is not null)
                    {
                        break;
                    }

                    throw ChunkReader.Truncated();
                }

                if (type == PngChunks.Iend)
                {
                    if (pixels is null)
                    {
                        throw new InvalidDataException("PNG: the file has no image data (IDAT chunk missing).");
                    }

                    break;
                }

                if (type == PngChunks.Idat && pixels is null)
                {
                    pixels = DecodeImageData(reader, length, header, state, cancellationToken);
                }
                else
                {
                    ProcessChunk(reader, type, length, state, beforeImage: pixels is null);
                }
            }

            return new DecodedImage(pixels, state.Metadata, state.Animated ? ImageFormat.Apng : ImageFormat.Png, state.FrameCount, state.SignificantBits);
        }
        catch
        {
            pixels?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the metadata of a PNG/APNG (ICC, EXIF from eXIf or a raw-profile text chunk, XMP, DPI) without decoding
    /// any image data.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a valid PNG, or a chunk it reads is corrupt.</exception>
    internal static ImageMetadata ReadMetadata(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reader = new ChunkReader(input);
        var state = new DecodeState(ReadHeader(reader));
        bool beforeImage = true;
        while (reader.TryReadHeader(out uint length, out uint type) && type != PngChunks.Iend)
        {
            if (type == PngChunks.Idat)
            {
                beforeImage = false;
                reader.Skip(length);
            }
            else
            {
                ProcessChunk(reader, type, length, state, beforeImage);
            }
        }

        return state.Metadata;
    }

    private static PngHeader ReadHeader(ChunkReader reader)
    {
        reader.ReadSignature();
        if (!reader.TryReadHeader(out uint length, out uint type) || type != PngChunks.Ihdr || length != 13)
        {
            throw new InvalidDataException("PNG: the IHDR chunk is missing or malformed.");
        }

        return PngHeader.Parse(reader.ReadData(length, type));
    }

    private static void ProcessChunk(ChunkReader reader, uint type, uint length, DecodeState state, bool beforeImage)
    {
        switch (type)
        {
            case PngChunks.Ihdr:
                throw new InvalidDataException("PNG: duplicate IHDR chunk.");
            case PngChunks.Plte when beforeImage:
                // Validated before reading: a forged length must not allocate. Only palette images need PLTE (a
                // truecolour image's suggested palette is ignored, as libpng does, when it is malformed).
                if (DecodeState.IsValidPaletteLength(length))
                {
                    state.SetPalette(reader.ReadData(length, type));
                }
                else if (state.NeedsPalette)
                {
                    throw new InvalidDataException("PNG: malformed PLTE chunk.");
                }
                else
                {
                    reader.Skip(length);
                }

                return;
            case PngChunks.Trns when beforeImage:
                // An invalid tRNS (wrong size for the colour type, or before PLTE) is ignored, as libpng does.
                if (state.IsValidTransparencyLength(length))
                {
                    state.SetTransparency(reader.ReadData(length, type));
                }
                else
                {
                    reader.Skip(length);
                }

                return;
            case PngChunks.Actl when beforeImage && length == 8:
                uint frames = BinaryPrimitives.ReadUInt32BigEndian(reader.ReadData(length, type));
                state.Animated = true;
                state.FrameCount = (int)Math.Clamp(frames, 1, int.MaxValue);
                return;
            case PngChunks.Sbit when beforeImage && length <= 4:
                state.SetSignificantBits(reader.ReadData(length, type));
                return;
            case PngChunks.Phys when length == 9:
                state.SetPhysical(reader.ReadData(length, type));
                return;
            // The first chunk of each kind wins; later ones are skipped (still consumed, so the chunk framing holds).
            case PngChunks.Iccp when beforeImage && length <= MaxMetadataBytes && state.Metadata.Icc is null:
                state.Metadata.Icc = ParseIccp(reader.ReadData(length, type));
                return;
            case PngChunks.Exif when length <= MaxMetadataBytes && state.Metadata.Exif is null:
                state.Metadata.Exif = ParseExif(reader.ReadData(length, type));
                return;
            case PngChunks.Itxt or PngChunks.Text or PngChunks.Ztxt when length <= MaxMetadataBytes && (state.Metadata.Exif is null || state.Metadata.Xmp is null):
                ParseText(type, reader.ReadData(length, type), state.Metadata);
                return;
            case PngChunks.Plte or PngChunks.Idat:
                // A late palette or stray IDAT after the image is complete: nothing left to use it for.
                reader.Skip(length);
                return;
            default:
                if (PngChunks.IsCritical(type))
                {
                    throw new InvalidDataException($"PNG: unknown critical chunk '{PngChunks.Name(type)}'.");
                }

                reader.Skip(length);
                return;
        }
    }

    private static PixelBuffer DecodeImageData(ChunkReader reader, uint firstLength, PngHeader header, DecodeState state, CancellationToken ct)
    {
        if (header.ColorType == PngColorType.Palette && state.Palette is null)
        {
            throw new InvalidDataException("PNG: palette image without a PLTE chunk.");
        }

        // Inflate expands at most 1032:1, so the bytes left in the file bound the raster a genuine file can hold:
        // a forged IHDR is refused before its buffer is allocated.
        if (reader.Remaining is { } remaining && header.FilteredLength() / DecodeLimits.MaxDeflateRatio > remaining)
        {
            throw new InvalidDataException($"PNG: the file is too small to hold a {header.Width}×{header.Height} image (truncated or forged header).");
        }

        var px = DecodeLimits.Allocate(header.Width, header.Height, header.BitDepth == 16 ? SampleDepth.Sixteen : SampleDepth.Eight, clear: false, "PNG");
        var idat = new IdatStream(reader, firstLength);
        try
        {
            using (var z = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
            {
                DecodeRows(z, idat, px, header, new RowConverter(header, state), ct);

                // Drive inflate to the end of the zlib stream so the Adler-32 trailer is verified.
                Span<byte> probe = stackalloc byte[1];
                _ = ReadInflated(z, idat, probe);
            }

            idat.Drain();
            return px;
        }
        catch
        {
            px.Dispose();
            throw;
        }
    }

    private static void DecodeRows(ZLibStream z, IdatStream idat, PixelBuffer px, PngHeader header, RowConverter converter, CancellationToken ct)
    {
        int bpp = Math.Max(1, header.BitsPerPixel / 8);
        int maxRowBytes = header.RowBytes(header.Width);
        byte[] cur = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
        byte[] prev = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
        try
        {
            foreach (var pass in Adam7.Passes(header.Width, header.Height, header.Interlaced))
            {
                int rowBytes = header.RowBytes(pass.Width);
                prev.AsSpan(0, rowBytes + 1).Clear();
                for (int r = 0; r < pass.Height; r++)
                {
                    if ((r & 63) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    Span<byte> line = cur.AsSpan(0, rowBytes + 1);
                    if (ReadInflated(z, idat, line) < line.Length)
                    {
                        throw new InvalidDataException("PNG: the image data ends early (the file is truncated or corrupt).");
                    }

                    PngFilters.Unfilter(line[0], line[1..], prev.AsSpan(1, rowBytes), bpp);
                    converter.Convert(line[1..], px, pass.YStart + (r * pass.YStep), pass.XStart, pass.XStep, pass.Width);
                    (cur, prev) = (prev, cur);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cur);
            ArrayPool<byte>.Shared.Return(prev);
        }
    }

    private static int ReadInflated(ZLibStream z, IdatStream idat, Span<byte> buffer)
    {
        try
        {
            return z.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (InvalidDataException ex) when (!idat.Faulted)
        {
            // Report a damaged chunk (CRC mismatch) in preference to the inflate error it caused.
            idat.Drain();
            throw new InvalidDataException("PNG: the compressed image data is corrupt.", ex);
        }
    }

    private static byte[]? ParseIccp(byte[] data)
    {
        int nul = data.AsSpan(0, Math.Min(data.Length, 80)).IndexOf((byte)0);
        if (nul < 1 || nul + 2 > data.Length || data[nul + 1] != 0)
        {
            return null;
        }

        return Inflate(data.AsSpan(nul + 2));
    }

    private static byte[]? ParseExif(byte[] data)
    {
        ReadOnlySpan<byte> d = data;
        if (d.StartsWith("Exif\0\0"u8))
        {
            d = d[6..];
        }

        return d.Length >= 8 && (d.StartsWith("II*\0"u8) || d.StartsWith("MM\0*"u8)) ? d.ToArray() : null;
    }

    // Text chunks carry XMP (iTXt "XML:com.adobe.xmp") and, by the ImageMagick convention used by libjxl and others,
    // hex-encoded "Raw profile type exif|xmp" payloads.
    private static void ParseText(uint type, byte[] data, ImageMetadata metadata)
    {
        ReadOnlySpan<byte> d = data;
        int nul = d.IndexOf((byte)0);
        if (nul < 1)
        {
            return;
        }

        string keyword = Encoding.Latin1.GetString(d[..nul]);
        bool exif = keyword is "Raw profile type exif" or "Raw profile type APP1";
        bool xmp = keyword is PngChunks.XmpKeyword or "Raw profile type xmp";

        // Only the first payload of each kind is used: nothing is inflated for comments or for a slot already filled.
        if (exif ? metadata.Exif is not null : !xmp || metadata.Xmp is not null)
        {
            return;
        }

        ReadOnlySpan<byte> rest = d[(nul + 1)..];
        byte[]? text = type switch
        {
            PngChunks.Text => rest.ToArray(),
            PngChunks.Ztxt => rest.Length > 1 && rest[0] == 0 ? Inflate(rest[1..]) : null,
            _ => ParseItxtText(rest),
        };

        if (text is null)
        {
            return;
        }

        if (keyword == PngChunks.XmpKeyword)
        {
            metadata.Xmp = text;
        }
        else if (exif)
        {
            metadata.Exif = ParseRawProfile(text) is { } raw ? ParseExif(raw) : null;
        }
        else
        {
            metadata.Xmp = ParseRawProfile(text);
        }
    }

    // iTXt after the keyword: compression flag, method, language tag NUL, translated keyword NUL, text.
    private static byte[]? ParseItxtText(ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 2)
        {
            return null;
        }

        bool compressed = rest[0] != 0;
        rest = rest[2..];
        int language = rest.IndexOf((byte)0);
        if (language < 0)
        {
            return null;
        }

        rest = rest[(language + 1)..];
        int translated = rest.IndexOf((byte)0);
        if (translated < 0)
        {
            return null;
        }

        rest = rest[(translated + 1)..];
        return compressed ? Inflate(rest) : rest.ToArray();
    }

    // Layout: newline, profile name, newline, decimal length, newline, hex digits split by any whitespace.
    private static byte[]? ParseRawProfile(ReadOnlySpan<byte> text)
    {
        int i = 0;
        SkipSpace(text, ref i);
        while (i < text.Length && !IsSpace(text[i]))
        {
            i++;
        }

        SkipSpace(text, ref i);
        long length = 0;
        while (i < text.Length && text[i] is >= (byte)'0' and <= (byte)'9')
        {
            length = (length * 10) + (text[i++] - '0');
            if (length > MaxMetadataBytes)
            {
                return null;
            }
        }

        // Two hex digits per byte must follow: a forged length never allocates more than the text could fill.
        if (length == 0 || length > (text.Length - i) / 2)
        {
            return null;
        }

        var bytes = new byte[length];
        int count = 0, high = -1;
        for (; i < text.Length && count < length; i++)
        {
            int v = HexValue(text[i]);
            if (v < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                bytes[count++] = (byte)((high << 4) | v);
                high = -1;
            }
        }

        return count == length ? bytes : null;

        static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

        static void SkipSpace(ReadOnlySpan<byte> t, ref int i)
        {
            while (i < t.Length && IsSpace(t[i]))
            {
                i++;
            }
        }

        static int HexValue(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };
    }

    // Inflates a zlib stream (metadata), refusing outputs over the metadata cap; null when corrupt.
    private static byte[]? Inflate(ReadOnlySpan<byte> zlib)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(zlib.ToArray()), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > MaxMetadataBytes)
                {
                    return null;
                }
            }

            return output.Length > 0 ? output.ToArray() : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The IHDR fields.</summary>
    private sealed record PngHeader(int Width, int Height, int BitDepth, PngColorType ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch
        {
            PngColorType.Gray or PngColorType.Palette => 1,
            PngColorType.GrayAlpha => 2,
            PngColorType.Rgb => 3,
            _ => 4,
        };

        public int BitsPerPixel => Channels * BitDepth;

        public int RowBytes(int width) => checked((int)((((long)width * BitsPerPixel) + 7) / 8));

        /// <summary>Bytes of filtered scanlines (filter byte + row, over all Adam7 passes) the zlib stream must inflate to.</summary>
        public long FilteredLength()
        {
            long total = 0;
            foreach (var pass in Adam7.Passes(Width, Height, Interlaced))
            {
                total += pass.Height * (RowBytes(pass.Width) + 1L);
            }

            return total;
        }

        public static PngHeader Parse(byte[] d)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d), h = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
            if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
            {
                throw new InvalidDataException($"PNG: invalid image size {w}×{h}.");
            }

            var type = (PngColorType)d[9];
            if (!Enum.IsDefined(type) || !PngLayout.IsLegal(type, d[8]))
            {
                throw new InvalidDataException($"PNG: invalid colour type {d[9]} with bit depth {d[8]}.");
            }

            if (d[10] != 0 || d[11] != 0 || d[12] > 1)
            {
                throw new InvalidDataException("PNG: unsupported compression, filter or interlace method.");
            }

            var header = new PngHeader((int)w, (int)h, d[8], type, d[12] == 1);

            // Scanlines must fit an array and the decoded row (4 or 8 bytes per pixel) a PixelBuffer row.
            if ((((long)w * header.BitsPerPixel) + 7) / 8 + 1 > Array.MaxLength || (long)w * (header.BitDepth == 16 ? 8 : 4) > int.MaxValue)
            {
                throw new InvalidDataException($"PNG: rows of {w} pixels are too wide.");
            }

            return header;
        }
    }

    /// <summary>Everything gathered from ancillary chunks.</summary>
    private sealed class DecodeState(PngHeader header)
    {
        public ImageMetadata Metadata { get; } = new();

        public Rgba32[]? Palette { get; private set; }

        /// <summary>Raw sample value (at the IHDR bit depth) marked transparent by tRNS for gray/RGB images.</summary>
        public (ushort R, ushort G, ushort B)? Key { get; private set; }

        public bool Animated { get; set; }

        public int FrameCount { get; set; } = 1;

        public int SignificantBits { get; private set; } = header.ColorType == PngColorType.Palette ? 8 : header.BitDepth;

        public bool NeedsPalette => header.ColorType == PngColorType.Palette;

        /// <summary>1–256 RGB entries.</summary>
        public static bool IsValidPaletteLength(uint length) => length is > 0 and <= 768 && length % 3 == 0;

        /// <summary>Palette images: 1–256 alpha values after PLTE; gray: one 16-bit sample; RGB: three; alpha types: none.</summary>
        public bool IsValidTransparencyLength(uint length) => header.ColorType switch
        {
            PngColorType.Palette => Palette is not null && length is > 0 and <= 256,
            PngColorType.Gray => length == 2,
            PngColorType.Rgb => length == 6,
            _ => false,
        };

        public void SetPalette(byte[] d)
        {
            var palette = new Rgba32[d.Length / 3];
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = new Rgba32(d[i * 3], d[(i * 3) + 1], d[(i * 3) + 2]);
            }

            if (Palette is { } previous)
            {
                // tRNS cannot precede PLTE legally, but keep any alpha already applied.
                for (int i = 0; i < Math.Min(previous.Length, palette.Length); i++)
                {
                    palette[i].A = previous[i].A;
                }
            }

            Palette = palette;
        }

        public void SetTransparency(byte[] d)
        {
            switch (header.ColorType)
            {
                case PngColorType.Palette when Palette is not null:
                    for (int i = 0; i < Math.Min(d.Length, Palette.Length); i++)
                    {
                        Palette[i].A = d[i];
                    }

                    break;
                case PngColorType.Gray when d.Length >= 2:
                    ushort g = BinaryPrimitives.ReadUInt16BigEndian(d);
                    Key = (g, g, g);
                    break;
                case PngColorType.Rgb when d.Length >= 6:
                    Key = (BinaryPrimitives.ReadUInt16BigEndian(d), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4)));
                    break;
            }
        }

        public void SetSignificantBits(byte[] d)
        {
            int limit = header.ColorType == PngColorType.Palette ? 8 : header.BitDepth;
            int expected = header.ColorType switch
            {
                PngColorType.Gray => 1,
                PngColorType.GrayAlpha => 2,
                PngColorType.Rgba => 4,
                _ => 3,
            };
            if (d.Length != expected)
            {
                return;
            }

            int max = 0;
            foreach (byte b in d)
            {
                if (b == 0 || b > limit)
                {
                    return;
                }

                max = Math.Max(max, b);
            }

            SignificantBits = max;
        }

        public void SetPhysical(byte[] d)
        {
            uint x = BinaryPrimitives.ReadUInt32BigEndian(d), y = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
            if (d[8] == 1 && x > 0 && y > 0)
            {
                Metadata.DpiX = Math.Round(x * 0.0254, 2);
                Metadata.DpiY = Math.Round(y * 0.0254, 2);
            }
        }
    }

    /// <summary>Converts unfiltered scanlines into RGBA pixels (8-bit, or 16-bit for 16-bit sources).</summary>
    private sealed class RowConverter
    {
        private readonly PngHeader _header;
        private readonly Rgba32[] _lut = new Rgba32[256];
        private readonly bool _hasKey;
        private readonly ushort _kr, _kg, _kb;

        public RowConverter(PngHeader header, DecodeState state)
        {
            _header = header;
            if (state.Palette is { } palette)
            {
                // Out-of-range indices decode as opaque black (as libpng does).
                _lut.AsSpan().Fill(new Rgba32(0, 0, 0));
                palette.CopyTo(_lut, 0);
            }

            if (state.Key is { } key)
            {
                _hasKey = true;
                (_kr, _kg, _kb) = key;
            }
        }

        public void Convert(ReadOnlySpan<byte> raw, PixelBuffer px, int y, int x0, int dx, int count)
        {
            if (px.Depth == SampleDepth.Sixteen)
            {
                Convert16(raw, px.Row64(y), x0, dx, count);
            }
            else
            {
                Convert8(raw, px.Row32(y), x0, dx, count);
            }
        }

        private void Convert8(ReadOnlySpan<byte> raw, Span<Rgba32> dst, int x0, int dx, int count)
        {
            switch (_header.ColorType)
            {
                case PngColorType.Rgba:
                    if (dx == 1)
                    {
                        MemoryMarshal.Cast<byte, Rgba32>(raw[..(count * 4)]).CopyTo(dst.Slice(x0, count));
                    }
                    else
                    {
                        for (int i = 0, x = x0; i < count; i++, x += dx)
                        {
                            dst[x] = MemoryMarshal.Read<Rgba32>(raw[(i * 4)..]);
                        }
                    }

                    break;

                case PngColorType.Rgb when dx == 1 && !_hasKey:
                    ExpandRgb(raw, MemoryMarshal.Cast<Rgba32, uint>(dst.Slice(x0, count)));
                    break;

                case PngColorType.Rgb:
                    for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 3)
                    {
                        byte r = raw[o], g = raw[o + 1], b = raw[o + 2];
                        dst[x] = new Rgba32(r, g, b, _hasKey && r == _kr && g == _kg && b == _kb ? (byte)0 : (byte)255);
                    }

                    break;

                case PngColorType.GrayAlpha:
                    for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 2)
                    {
                        byte v = raw[o];
                        dst[x] = new Rgba32(v, v, v, raw[o + 1]);
                    }

                    break;

                case PngColorType.Gray:
                {
                    int depth = _header.BitDepth, mask = (1 << depth) - 1, scale = 255 / mask;
                    for (int i = 0, x = x0; i < count; i++, x += dx)
                    {
                        int v = depth == 8 ? raw[i] : Sample(raw, i, depth, mask);
                        byte g = (byte)(v * scale);
                        dst[x] = new Rgba32(g, g, g, _hasKey && v == _kr ? (byte)0 : (byte)255);
                    }

                    break;
                }

                default:
                {
                    int depth = _header.BitDepth, mask = (1 << depth) - 1;
                    for (int i = 0, x = x0; i < count; i++, x += dx)
                    {
                        dst[x] = _lut[depth == 8 ? raw[i] : Sample(raw, i, depth, mask)];
                    }

                    break;
                }
            }
        }

        private void Convert16(ReadOnlySpan<byte> raw, Span<Rgba64> dst, int x0, int dx, int count)
        {
            switch (_header.ColorType)
            {
                case PngColorType.Rgba:
                    for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 8)
                    {
                        dst[x] = new Rgba64(Be(raw, o), Be(raw, o + 2), Be(raw, o + 4), Be(raw, o + 6));
                    }

                    break;

                case PngColorType.Rgb:
                    for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 6)
                    {
                        ushort r = Be(raw, o), g = Be(raw, o + 2), b = Be(raw, o + 4);
                        dst[x] = new Rgba64(r, g, b, _hasKey && r == _kr && g == _kg && b == _kb ? (ushort)0 : ushort.MaxValue);
                    }

                    break;

                case PngColorType.GrayAlpha:
                    for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 4)
                    {
                        ushort v = Be(raw, o);
                        dst[x] = new Rgba64(v, v, v, Be(raw, o + 2));
                    }

                    break;

                default:
                    for (int i = 0, x = x0; i < count; i++, x += dx)
                    {
                        ushort v = Be(raw, i * 2);
                        dst[x] = new Rgba64(v, v, v, _hasKey && v == _kr ? (ushort)0 : ushort.MaxValue);
                    }

                    break;
            }
        }

        // RGB → opaque RGBA, the most common photo layout (little-endian RGBA words).
        private static unsafe void ExpandRgb(ReadOnlySpan<byte> raw, Span<uint> dst)
        {
            fixed (byte* s = raw)
            fixed (uint* d = dst)
            {
                int i = 0;
                for (; i + 1 < dst.Length; i++)
                {
                    // Reading 4 bytes is safe while another pixel follows; the alpha byte is overwritten.
                    d[i] = Unsafe.ReadUnaligned<uint>(s + (i * 3)) | 0xFF000000;
                }

                for (; i < dst.Length; i++)
                {
                    byte* p = s + (i * 3);
                    d[i] = (uint)(p[0] | (p[1] << 8) | (p[2] << 16)) | 0xFF000000;
                }
            }
        }

        private static ushort Be(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt16BigEndian(raw[offset..]);

        // The i-th packed sample of a 1/2/4-bit row (MSB first).
        private static int Sample(ReadOnlySpan<byte> raw, int i, int depth, int mask)
        {
            int bit = i * depth;
            return (raw[bit >> 3] >> (8 - depth - (bit & 7))) & mask;
        }
    }

    /// <summary>Reads chunk framing from the input, with one chunk header of look-ahead.</summary>
    private sealed class ChunkReader(Stream stream)
    {
        private (uint Length, uint Type)? _pending;
        private byte[]? _scratch;

        public static InvalidDataException Truncated() => new("PNG: the file is truncated (unexpected end of data).");

        /// <summary>Bytes left after the current position, when the stream can tell.</summary>
        public long? Remaining => stream.CanSeek ? Math.Max(0, stream.Length - stream.Position) : null;

        public void ReadSignature()
        {
            Span<byte> signature = stackalloc byte[8];
            if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) < 8 || !signature.SequenceEqual(PngChunks.Signature))
            {
                throw new InvalidDataException("Not a PNG file (the signature is missing or damaged).");
            }
        }

        /// <summary>Reads the next chunk header; false (type 0) at the end of the stream.</summary>
        public bool TryReadHeader(out uint length, out uint type)
        {
            if (_pending is { } pending)
            {
                _pending = null;
                (length, type) = pending;
                return true;
            }

            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
            {
                length = type = 0;
                return false;
            }

            length = BinaryPrimitives.ReadUInt32BigEndian(header);
            type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (length > PngChunks.MaxLength)
            {
                throw new InvalidDataException("PNG: invalid chunk length (the file is corrupt).");
            }

            return true;
        }

        public void PushBack(uint length, uint type) => _pending = (length, type);

        /// <summary>Reads a chunk payload and verifies its CRC.</summary>
        public byte[] ReadData(uint length, uint type)
        {
            // A length beyond the end of the file is a truncation: refused before allocating.
            if (Remaining is { } remaining && length + 4L > remaining)
            {
                throw Truncated();
            }

            var data = new byte[length];
            ReadExactly(data);
            Span<byte> crc = stackalloc byte[4];
            ReadExactly(crc);
            Span<byte> typeBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(typeBytes, type);
            if (BinaryPrimitives.ReadUInt32BigEndian(crc) != Crc32.Append(Crc32.Compute(typeBytes), data))
            {
                throw new InvalidDataException($"PNG: bad CRC in {PngChunks.Name(type)} chunk (the file is corrupt).");
            }

            return data;
        }

        /// <summary>Skips a chunk payload and its CRC (seeking when possible; a short skip surfaces at the next header).</summary>
        public void Skip(uint length)
        {
            long total = length + 4L;
            if (stream.CanSeek)
            {
                stream.Seek(total, SeekOrigin.Current);
                return;
            }

            _scratch ??= new byte[16 * 1024];
            while (total > 0)
            {
                int n = stream.Read(_scratch, 0, (int)Math.Min(total, _scratch.Length));
                if (n == 0)
                {
                    return;
                }

                total -= n;
            }
        }

        public void ReadExactly(Span<byte> buffer)
        {
            if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            {
                throw Truncated();
            }
        }

        public int ReadSome(Span<byte> buffer)
        {
            int n = stream.Read(buffer);
            return n > 0 ? n : throw Truncated();
        }
    }

    /// <summary>The payload of consecutive IDAT chunks as one stream, CRC-checked chunk by chunk.</summary>
    private sealed class IdatStream : Stream
    {
        private static readonly byte[] s_type = [0x49, 0x44, 0x41, 0x54];
        private readonly ChunkReader _reader;
        private uint _remaining;
        private uint _crc;
        private bool _ended;

        public IdatStream(ChunkReader reader, uint firstLength)
        {
            _reader = reader;
            Begin(firstLength);
        }

        /// <summary>True once this stream itself failed (truncation or CRC), so the error is already precise.</summary>
        public bool Faulted { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            try
            {
                while (_remaining == 0)
                {
                    if (_ended)
                    {
                        return 0;
                    }

                    if (!_reader.TryReadHeader(out uint length, out uint type))
                    {
                        _ended = true;
                        return 0;
                    }

                    if (type != PngChunks.Idat)
                    {
                        _reader.PushBack(length, type);
                        _ended = true;
                        return 0;
                    }

                    Begin(length);
                }

                int n = _reader.ReadSome(buffer[..(int)Math.Min((uint)buffer.Length, _remaining)]);
                _crc = Crc32.Append(_crc, buffer[..n]);
                _remaining -= (uint)n;
                if (_remaining == 0)
                {
                    VerifyCrc();
                }

                return n;
            }
            catch (InvalidDataException)
            {
                Faulted = true;
                throw;
            }
        }

        /// <summary>Reads (and CRC-checks) whatever remains of the IDAT sequence.</summary>
        public void Drain()
        {
            var scratch = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (Read(scratch) > 0)
                {
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Begin(uint length)
        {
            _remaining = length;
            _crc = Crc32.Compute(s_type);
            if (length == 0)
            {
                VerifyCrc();
            }
        }

        private void VerifyCrc()
        {
            Span<byte> crc = stackalloc byte[4];
            _reader.ReadExactly(crc);
            if (BinaryPrimitives.ReadUInt32BigEndian(crc) != _crc)
            {
                throw new InvalidDataException("PNG: bad CRC in IDAT chunk (the file is corrupt).");
            }
        }
    }
}
