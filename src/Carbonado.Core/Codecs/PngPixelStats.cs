using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Exact facts about a pixel buffer that decide which PNG representations are lossless, gathered in one pass
/// (plus a second pass only to validate a transparent-colour key).
/// </summary>
internal sealed class PngPixelStats
{
    private PngPixelStats()
    {
    }

    /// <summary>Every alpha sample is at its maximum.</summary>
    public bool IsOpaque { get; private init; }

    /// <summary>R = G = B for every pixel (transparent ones included).</summary>
    public bool IsGray { get; private init; }

    /// <summary>Every 16-bit sample is v·257, i.e. exactly representable in 8 bits (always true for 8-bit buffers).</summary>
    public bool Fits8 { get; private init; }

    /// <summary>Every alpha sample is 0 or the maximum.</summary>
    public bool IsBinaryAlpha { get; private init; }

    /// <summary>
    /// When alpha is binary: the RGB shared by every transparent pixel, provided no opaque pixel uses it, so a tRNS
    /// key can replace the alpha channel. Sample units of the buffer depth; null otherwise.
    /// </summary>
    public Rgba64? TransparentKey { get; private init; }

    /// <summary>For gray images that fit 8 bits: the smallest of 1, 2, 4, 8 bits holding every gray level exactly; else 16.</summary>
    public int GrayBitsNeeded { get; private init; }

    /// <summary>The distinct colours (8-bit RGBA) when there are at most 256 and they fit 8 bits; null otherwise or when not collected.</summary>
    public Rgba32[]? Colors { get; private init; }

    public static PngPixelStats Collect(PixelBuffer px, bool collectColors) =>
        px.Depth == SampleDepth.Eight ? Collect8(px, collectColors) : Collect16(px, collectColors);

    /// <summary>Bits needed for an 8-bit gray level: 1 for {0, 255}, 2 for multiples of 85, 4 for multiples of 17, else 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GrayBits(int v) => v % 17 != 0 ? 8 : v % 85 != 0 ? 4 : v is 0 or 255 ? 1 : 2;

    private static PngPixelStats Collect8(PixelBuffer px, bool collectColors)
    {
        bool opaque = true, gray = true, binary = true, keyConsistent = true, haveKey = false;
        uint key = 0;
        int grayBits = 1;
        var colors = collectColors ? new HashSet<uint>() : null;
        uint lastColor = 0;
        bool haveLast = false;

        for (int y = 0; y < px.Height; y++)
        {
            Span<Rgba32> row = px.Row32(y);
            if (opaque && !gray && colors is null)
            {
                // Only opacity is still undecided: vectorised scan, fall through to the full loop on the first miss.
                if (AllOpaque(row))
                {
                    continue;
                }
            }
            else if (!opaque && !binary && !gray && colors is null)
            {
                break;
            }

            foreach (Rgba32 p in row)
            {
                if (p.A != 255)
                {
                    opaque = false;
                    if (p.A != 0)
                    {
                        binary = false;
                    }
                    else
                    {
                        uint rgb = (uint)(p.R | (p.G << 8) | (p.B << 16));
                        if (!haveKey)
                        {
                            key = rgb;
                            haveKey = true;
                        }
                        else if (rgb != key)
                        {
                            keyConsistent = false;
                        }
                    }
                }

                if (gray)
                {
                    if (p.R != p.G || p.R != p.B)
                    {
                        gray = false;
                    }
                    else if (grayBits < 8)
                    {
                        grayBits = Math.Max(grayBits, GrayBits(p.R));
                    }
                }

                if (colors is not null)
                {
                    uint packed = Pack(p);
                    if (!haveLast || packed != lastColor)
                    {
                        lastColor = packed;
                        haveLast = true;
                        if (colors.Add(packed) && colors.Count > 256)
                        {
                            colors = null;
                        }
                    }
                }
            }
        }

        Rgba64? transparentKey = null;
        if (binary && !opaque && haveKey && keyConsistent && !OpaqueUsesKey8(px, key))
        {
            transparentKey = new Rgba64((ushort)(key & 0xFF), (ushort)((key >> 8) & 0xFF), (ushort)((key >> 16) & 0xFF), 0);
        }

        return new PngPixelStats
        {
            IsOpaque = opaque,
            IsGray = gray,
            Fits8 = true,
            IsBinaryAlpha = binary,
            TransparentKey = transparentKey,
            GrayBitsNeeded = gray ? grayBits : 8,
            Colors = colors?.Select(Unpack).ToArray(),
        };
    }

    private static PngPixelStats Collect16(PixelBuffer px, bool collectColors)
    {
        bool opaque = true, gray = true, binary = true, fits8 = true, keyConsistent = true, haveKey = false;
        Rgba64 key = default;
        int grayBits = 1;
        var colors = collectColors ? new HashSet<uint>() : null;

        for (int y = 0; y < px.Height; y++)
        {
            if (!opaque && !binary && !gray && !fits8)
            {
                break;
            }

            foreach (Rgba64 p in px.Row64(y))
            {
                if (fits8 && !(Fits(p.R) && Fits(p.G) && Fits(p.B) && Fits(p.A)))
                {
                    fits8 = false;
                    colors = null;
                }

                if (p.A != ushort.MaxValue)
                {
                    opaque = false;
                    if (p.A != 0)
                    {
                        binary = false;
                    }
                    else if (!haveKey)
                    {
                        key = p with { A = 0 };
                        haveKey = true;
                    }
                    else if (p.R != key.R || p.G != key.G || p.B != key.B)
                    {
                        keyConsistent = false;
                    }
                }

                if (gray)
                {
                    if (p.R != p.G || p.R != p.B)
                    {
                        gray = false;
                    }
                    else if (grayBits < 8)
                    {
                        grayBits = Math.Max(grayBits, GrayBits(p.R >> 8));
                    }
                }

                if (colors is not null)
                {
                    uint packed = Pack(new Rgba32((byte)(p.R >> 8), (byte)(p.G >> 8), (byte)(p.B >> 8), (byte)(p.A >> 8)));
                    if (colors.Add(packed) && colors.Count > 256)
                    {
                        colors = null;
                    }
                }
            }
        }

        Rgba64? transparentKey = binary && !opaque && haveKey && keyConsistent && !OpaqueUsesKey16(px, key) ? key : null;
        return new PngPixelStats
        {
            IsOpaque = opaque,
            IsGray = gray,
            Fits8 = fits8,
            IsBinaryAlpha = binary,
            TransparentKey = transparentKey,
            GrayBitsNeeded = gray && fits8 ? grayBits : 16,
            Colors = colors?.Select(Unpack).ToArray(),
        };

        static bool Fits(ushort v) => (v >> 8) == (v & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(Rgba32 p) => (uint)(p.R | (p.G << 8) | (p.B << 16) | (p.A << 24));

    private static Rgba32 Unpack(uint v) => new((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));

    private static bool AllOpaque(Span<Rgba32> row)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<Rgba32, uint>(row);
        const uint AlphaMask = 0xFF00_0000;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var mask = Vector256.Create(AlphaMask);
            ref uint start = ref MemoryMarshal.GetReference(words);
            for (; i + 8 <= words.Length; i += 8)
            {
                if ((Vector256.LoadUnsafe(ref start, (nuint)i) & mask) != mask)
                {
                    return false;
                }
            }
        }

        for (; i < words.Length; i++)
        {
            if ((words[i] & AlphaMask) != AlphaMask)
            {
                return false;
            }
        }

        return true;
    }

    private static bool OpaqueUsesKey8(PixelBuffer px, uint key)
    {
        for (int y = 0; y < px.Height; y++)
        {
            foreach (Rgba32 p in px.Row32(y))
            {
                if (p.A != 0 && (uint)(p.R | (p.G << 8) | (p.B << 16)) == key)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool OpaqueUsesKey16(PixelBuffer px, Rgba64 key)
    {
        for (int y = 0; y < px.Height; y++)
        {
            foreach (Rgba64 p in px.Row64(y))
            {
                if (p.A != 0 && p.R == key.R && p.G == key.G && p.B == key.B)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
