using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Carbonado.Core.Imaging;

/// <summary>Alpha-channel queries and edits on <see cref="PixelBuffer"/>s.</summary>
public static class AlphaOps
{
    private const uint Alpha8Mask = 0xFF000000u;
    private const ulong Alpha16Mask = 0xFFFF_0000_0000_0000ul;

    /// <summary>True when every pixel has the maximum alpha (255 or 65535).</summary>
    public static bool IsOpaque(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        for (int y = 0; y < pixels.Height; y++)
        {
            bool opaque = pixels.Depth == SampleDepth.Eight
                ? RowIsOpaque(MemoryMarshal.Cast<byte, uint>(pixels.RowBytes(y)), Alpha8Mask)
                : RowIsOpaque(MemoryMarshal.Cast<byte, ulong>(pixels.RowBytes(y)), Alpha16Mask);
            if (!opaque)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sets R, G and B to 0 wherever alpha is 0 (invisible pixels), in place.</summary>
    public static void CleanInvisible(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        for (int y = 0; y < pixels.Height; y++)
        {
            if (pixels.Depth == SampleDepth.Eight)
            {
                CleanRow(MemoryMarshal.Cast<byte, uint>(pixels.RowBytes(y)), Alpha8Mask);
            }
            else
            {
                CleanRow(MemoryMarshal.Cast<byte, ulong>(pixels.RowBytes(y)), Alpha16Mask);
            }
        }
    }

    /// <summary>
    /// Composites the pixels over an opaque <paramref name="background"/> (its alpha is ignored) and returns a new,
    /// fully opaque buffer of the same depth. Blending happens on the encoded (sRGB) values, as browsers and image
    /// viewers do, with exact integer rounding.
    /// </summary>
    public static PixelBuffer Flatten(PixelBuffer pixels, Rgba32 background)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var result = PixelBuffer.Create(pixels.Width, pixels.Height, pixels.Depth, clear: false);
        try
        {
            Parallel.For(0, pixels.Height, y =>
            {
                if (pixels.Depth == SampleDepth.Eight)
                {
                    FlattenRow(pixels.Row32(y), result.Row32(y), background);
                }
                else
                {
                    FlattenRow(pixels.Row64(y), result.Row64(y), background);
                }
            });
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool RowIsOpaque<T>(ReadOnlySpan<T> row, T alphaMask)
        where T : unmanaged, IEquatable<T>, System.Numerics.IBitwiseOperators<T, T, T>
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<T>.Count)
        {
            var mask = Vector256.Create(alphaMask);
            ref T start = ref MemoryMarshal.GetReference(row);
            for (; i <= row.Length - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                var v = Vector256.LoadUnsafe(ref start, (nuint)i);
                if (!Vector256.EqualsAll(v & mask, mask))
                {
                    return false;
                }
            }
        }

        for (; i < row.Length; i++)
        {
            if (!(row[i] & alphaMask).Equals(alphaMask))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CleanRow<T>(Span<T> row, T alphaMask)
        where T : unmanaged, IEquatable<T>, System.Numerics.IBitwiseOperators<T, T, T>
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<T>.Count)
        {
            var mask = Vector256.Create(alphaMask);
            ref T start = ref MemoryMarshal.GetReference(row);
            for (; i <= row.Length - Vector256<T>.Count; i += Vector256<T>.Count)
            {
                var v = Vector256.LoadUnsafe(ref start, (nuint)i);
                var invisible = Vector256.Equals(v & mask, Vector256<T>.Zero);
                Vector256.AndNot(v, invisible).StoreUnsafe(ref start, (nuint)i);
            }
        }

        for (; i < row.Length; i++)
        {
            if ((row[i] & alphaMask).Equals(default))
            {
                row[i] = default;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FlattenRow(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst, Rgba32 bg)
    {
        for (int x = 0; x < src.Length; x++)
        {
            Rgba32 p = src[x];
            int a = p.A, ia = 255 - a;
            dst[x] = new Rgba32(
                (byte)(((p.R * a) + (bg.R * ia) + 127) / 255),
                (byte)(((p.G * a) + (bg.G * ia) + 127) / 255),
                (byte)(((p.B * a) + (bg.B * ia) + 127) / 255),
                255);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FlattenRow(ReadOnlySpan<Rgba64> src, Span<Rgba64> dst, Rgba32 bg)
    {
        uint br = bg.R * 257u, bgG = bg.G * 257u, bb = bg.B * 257u;
        for (int x = 0; x < src.Length; x++)
        {
            Rgba64 p = src[x];
            uint a = p.A, ia = 65535u - a;
            dst[x] = new Rgba64(
                (ushort)(((p.R * a) + (br * ia) + 32767u) / 65535u),
                (ushort)(((p.G * a) + (bgG * ia) + 32767u) / 65535u),
                (ushort)(((p.B * a) + (bb * ia) + 32767u) / 65535u),
                65535);
        }
    }
}
