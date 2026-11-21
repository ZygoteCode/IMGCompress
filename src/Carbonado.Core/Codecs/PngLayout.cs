using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>A concrete PNG representation of a pixel buffer: colour type, bit depth, palette or transparency key.</summary>
internal sealed class PngLayout
{
    public required PngColorType ColorType { get; init; }

    public required int BitDepth { get; init; }

    public bool Interlace { get; init; }

    /// <summary>Palette entries (alpha in A), translucent entries first. Only for <see cref="PngColorType.Palette"/>.</summary>
    public Rgba32[]? Palette { get; init; }

    /// <summary>tRNS colour key for Gray/Rgb, in output sample units (gray uses R). Null when the image is opaque.</summary>
    public Rgba64? TransparentKey { get; init; }

    public int Channels => ColorType switch
    {
        PngColorType.Gray or PngColorType.Palette => 1,
        PngColorType.GrayAlpha => 2,
        PngColorType.Rgb => 3,
        _ => 4,
    };

    public int BitsPerPixel => Channels * BitDepth;

    /// <summary>Filter distance: bytes per complete pixel, at least 1.</summary>
    public int FilterDistance => Math.Max(1, BitsPerPixel / 8);

    public bool IsGrayType => ColorType is PngColorType.Gray or PngColorType.GrayAlpha;

    /// <summary>Bytes of one scanline of <paramref name="width"/> pixels (without the filter byte).</summary>
    public int RowBytes(int width) => checked((int)((((long)width * BitsPerPixel) + 7) / 8));

    public static bool IsLegal(PngColorType type, int depth) => type switch
    {
        PngColorType.Gray => depth is 1 or 2 or 4 or 8 or 16,
        PngColorType.Palette => depth is 1 or 2 or 4 or 8,
        PngColorType.Rgb or PngColorType.GrayAlpha or PngColorType.Rgba => depth is 8 or 16,
        _ => false,
    };

    /// <summary>
    /// Resolves the representation for <see cref="PngEncoder.Encode"/>: automatic when <paramref name="type"/> is null,
    /// otherwise validated to be exact.
    /// </summary>
    /// <param name="allowGray">False when an RGB colour profile must be kept (gray types would invalidate it).</param>
    /// <exception cref="ArgumentException">The requested type/depth cannot represent the pixels exactly or is illegal.</exception>
    public static PngLayout Plan(PngPixelStats stats, SampleDepth bufferDepth, PngColorType? type, int? depth, bool interlace, bool allowGray)
    {
        PngColorType t = type ?? (stats.IsGray && allowGray
            ? stats.IsOpaque ? PngColorType.Gray : PngColorType.GrayAlpha
            : stats.IsOpaque ? PngColorType.Rgb : PngColorType.Rgba);
        int d = depth ?? (t == PngColorType.Palette || bufferDepth == SampleDepth.Eight ? 8 : 16);
        if (!IsLegal(t, d))
        {
            throw new ArgumentException($"PNG colour type {t} does not allow bit depth {d}.", nameof(depth));
        }

        if (d < 16 && !stats.Fits8)
        {
            throw new ArgumentException($"The 16-bit samples cannot be stored exactly in {d} bits.", nameof(depth));
        }

        if ((t is PngColorType.Gray or PngColorType.GrayAlpha) && !stats.IsGray)
        {
            throw new ArgumentException("The pixels are not gray (R = G = B), so a gray colour type would lose colour.", nameof(type));
        }

        if (t == PngColorType.Gray && d < 8 && stats.GrayBitsNeeded > d)
        {
            throw new ArgumentException($"The gray levels need {stats.GrayBitsNeeded} bits; {d} would lose precision.", nameof(depth));
        }

        Rgba64? key = null;
        if ((t is PngColorType.Gray or PngColorType.Rgb) && !stats.IsOpaque)
        {
            key = stats.TransparentKey is { } k
                ? ToOutputUnits(k, bufferDepth, t == PngColorType.Gray ? d : Math.Max(d, 8))
                : throw new ArgumentException($"PNG colour type {t} has no alpha channel and the transparency cannot be expressed as a single colour key.", nameof(type));
        }

        Rgba32[]? palette = null;
        if (t == PngColorType.Palette)
        {
            palette = stats.Colors is { } colors && colors.Length <= (1 << d)
                ? SortPalette(colors)
                : throw new ArgumentException($"A {d}-bit palette holds at most {1 << d} colours; the image has more.", nameof(type));
        }

        return new PngLayout { ColorType = t, BitDepth = d, Interlace = interlace, Palette = palette, TransparentKey = key };
    }

    /// <summary>Palette order: translucent entries first (keeps tRNS short), then by luma, then by value (deterministic).</summary>
    public static Rgba32[] SortPalette(IEnumerable<Rgba32> colors) =>
        colors.OrderBy(c => c.A == 255 ? 1 : 0)
            .ThenBy(c => c.A)
            .ThenBy(c => (299 * c.R) + (587 * c.G) + (114 * c.B))
            .ThenBy(c => (c.R << 16) | (c.G << 8) | c.B)
            .ToArray();

    /// <summary>Smallest palette bit depth holding <paramref name="count"/> entries.</summary>
    public static int PaletteDepth(int count) => count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;

    // Converts a key from buffer units to the output depth (8-bit values for 1/2/4-bit gray are scaled down).
    private static Rgba64 ToOutputUnits(Rgba64 key, SampleDepth bufferDepth, int depth)
    {
        int Convert(int v)
        {
            int v8 = bufferDepth == SampleDepth.Eight ? v : v >> 8;
            return depth switch
            {
                16 => bufferDepth == SampleDepth.Sixteen ? v : v * 257,
                8 => v8,
                _ => v8 / (255 / ((1 << depth) - 1)),
            };
        }

        return new Rgba64((ushort)Convert(key.R), (ushort)Convert(key.G), (ushort)Convert(key.B), 0);
    }
}

/// <summary>
/// Converts pixel rows into PNG raw scanline bytes for one <see cref="PngLayout"/>. Not thread-safe (scratch buffers):
/// use one packer per thread.
/// </summary>
internal sealed class PngRowPacker
{
    private readonly PixelBuffer _px;
    private readonly PngLayout _layout;
    private readonly Dictionary<uint, byte>? _paletteIndex;
    private readonly Rgba32[]? _row32;
    private readonly Rgba64[]? _row64;

    public PngRowPacker(PixelBuffer px, PngLayout layout)
    {
        _px = px;
        _layout = layout;
        if (layout.Palette is { } palette)
        {
            _paletteIndex = new Dictionary<uint, byte>(palette.Length);
            for (int i = 0; i < palette.Length; i++)
            {
                _paletteIndex[Pack(palette[i])] = (byte)i;
            }
        }

        bool wide = layout.BitDepth == 16;
        if (wide && px.Depth == SampleDepth.Eight)
        {
            _row64 = new Rgba64[px.Width];
        }
        else if (!wide && px.Depth == SampleDepth.Sixteen)
        {
            _row32 = new Rgba32[px.Width];
        }
    }

    /// <summary>Packs <paramref name="count"/> pixels of row <paramref name="y"/> starting at <paramref name="x0"/>, step <paramref name="dx"/>.</summary>
    public void Pack(int y, int x0, int dx, int count, Span<byte> dst)
    {
        if (_layout.BitDepth == 16)
        {
            Pack16(Source64(y), x0, dx, count, dst);
        }
        else
        {
            Pack8(Source32(y), x0, dx, count, dst);
        }
    }

    private static uint Pack(Rgba32 p) => (uint)(p.R | (p.G << 8) | (p.B << 16) | (p.A << 24));

    private ReadOnlySpan<Rgba64> Source64(int y)
    {
        if (_row64 is null)
        {
            return _px.Row64(y);
        }

        Span<Rgba32> src = _px.Row32(y);
        for (int x = 0; x < src.Length; x++)
        {
            Rgba32 p = src[x];
            _row64[x] = new Rgba64((ushort)(p.R * 257), (ushort)(p.G * 257), (ushort)(p.B * 257), (ushort)(p.A * 257));
        }

        return _row64;
    }

    private ReadOnlySpan<Rgba32> Source32(int y)
    {
        if (_row32 is null)
        {
            return _px.Row32(y);
        }

        Span<Rgba64> src = _px.Row64(y);
        for (int x = 0; x < src.Length; x++)
        {
            Rgba64 p = src[x];
            _row32[x] = new Rgba32((byte)(p.R >> 8), (byte)(p.G >> 8), (byte)(p.B >> 8), (byte)(p.A >> 8));
        }

        return _row32;
    }

    private void Pack8(ReadOnlySpan<Rgba32> src, int x0, int dx, int count, Span<byte> dst)
    {
        switch (_layout.ColorType)
        {
            case PngColorType.Rgba:
                if (dx == 1)
                {
                    MemoryMarshal.AsBytes(src.Slice(x0, count)).CopyTo(dst);
                }
                else
                {
                    for (int i = 0, x = x0; i < count; i++, x += dx)
                    {
                        MemoryMarshal.Write(dst[(i * 4)..], src[x]);
                    }
                }

                break;

            case PngColorType.Rgb:
                for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 3)
                {
                    Rgba32 p = src[x];
                    dst[o] = p.R;
                    dst[o + 1] = p.G;
                    dst[o + 2] = p.B;
                }

                break;

            case PngColorType.GrayAlpha:
                for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += 2)
                {
                    dst[o] = src[x].R;
                    dst[o + 1] = src[x].A;
                }

                break;

            case PngColorType.Gray when _layout.BitDepth == 8:
                for (int i = 0, x = x0; i < count; i++, x += dx)
                {
                    dst[i] = src[x].R;
                }

                break;

            case PngColorType.Gray:
            {
                int divisor = 255 / ((1 << _layout.BitDepth) - 1);
                var bits = new BitPacker(dst, _layout.BitDepth);
                for (int i = 0, x = x0; i < count; i++, x += dx)
                {
                    bits.Add(src[x].R / divisor);
                }

                bits.Flush();
                break;
            }

            default:
            {
                Dictionary<uint, byte> index = _paletteIndex!;
                var bits = new BitPacker(dst, _layout.BitDepth);
                uint last = Pack(src[x0]);
                byte lastIndex = index[last];
                for (int i = 0, x = x0; i < count; i++, x += dx)
                {
                    uint c = Pack(src[x]);
                    if (c != last)
                    {
                        last = c;
                        lastIndex = index[c];
                    }

                    bits.Add(lastIndex);
                }

                bits.Flush();
                break;
            }
        }
    }

    private void Pack16(ReadOnlySpan<Rgba64> src, int x0, int dx, int count, Span<byte> dst)
    {
        int channels = _layout.Channels;
        for (int i = 0, x = x0, o = 0; i < count; i++, x += dx, o += channels * 2)
        {
            Rgba64 p = src[x];
            switch (_layout.ColorType)
            {
                case PngColorType.Rgba:
                    BinaryPrimitives.WriteUInt16BigEndian(dst[o..], p.R);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 2)..], p.G);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 4)..], p.B);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 6)..], p.A);
                    break;
                case PngColorType.Rgb:
                    BinaryPrimitives.WriteUInt16BigEndian(dst[o..], p.R);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 2)..], p.G);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 4)..], p.B);
                    break;
                case PngColorType.GrayAlpha:
                    BinaryPrimitives.WriteUInt16BigEndian(dst[o..], p.R);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(o + 2)..], p.A);
                    break;
                default:
                    BinaryPrimitives.WriteUInt16BigEndian(dst[o..], p.R);
                    break;
            }
        }
    }

    /// <summary>Packs 1/2/4/8-bit values MSB-first into bytes; the last byte is zero-padded.</summary>
    private ref struct BitPacker(Span<byte> dst, int depth)
    {
        private readonly Span<byte> _dst = dst;
        private int _acc;
        private int _bits;
        private int _pos;

        public void Add(int value)
        {
            _acc = (_acc << depth) | value;
            _bits += depth;
            if (_bits == 8)
            {
                _dst[_pos++] = (byte)_acc;
                _acc = 0;
                _bits = 0;
            }
        }

        public void Flush()
        {
            if (_bits > 0)
            {
                _dst[_pos++] = (byte)(_acc << (8 - _bits));
                _acc = 0;
                _bits = 0;
            }
        }
    }
}
