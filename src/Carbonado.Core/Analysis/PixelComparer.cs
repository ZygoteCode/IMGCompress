using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Analysis;

/// <summary>
/// Exact and "visible" pixel identity, a content hash, and PSNR. Buffers of different depths are compared through the
/// canonical 16-bit form (8-bit values × 257), so an 8-bit image equals its exact 16-bit expansion.
/// </summary>
public static class PixelComparer
{
    /// <summary>
    /// True when both buffers have the same size and pixels. With <paramref name="ignoreInvisibleRgb"/>, pixels whose
    /// alpha is 0 in both buffers count as equal whatever their RGB (their alpha must still match everywhere).
    /// </summary>
    public static bool Identical(PixelBuffer a, PixelBuffer b, bool ignoreInvisibleRgb)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        if (a.Depth == b.Depth)
        {
            for (int y = 0; y < a.Height; y++)
            {
                if (!a.RowBytes(y).SequenceEqual(b.RowBytes(y)) && (!ignoreInvisibleRgb || !RowsVisiblyEqual(a, b, y)))
                {
                    return false;
                }
            }

            return true;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!RowsVisiblyEqualMixed(a, b, y, ignoreInvisibleRgb))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SHA-256 of the image: width and height (little-endian int32) followed by every pixel as canonical RGBA16
    /// (little-endian, 8-bit samples × 257). With <paramref name="ignoreInvisibleRgb"/>, RGB is hashed as 0 where alpha is 0.
    /// Equal hashes ⇔ <see cref="Identical"/> with the same flag (barring SHA-256 collisions), across bit depths.
    /// </summary>
    public static byte[] Hash(PixelBuffer pixels, bool ignoreInvisibleRgb)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, pixels.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], pixels.Height);
        sha.AppendData(header);
        byte[] rented = ArrayPool<byte>.Shared.Rent(pixels.Width * 8);
        try
        {
            Span<ushort> canonical = MemoryMarshal.Cast<byte, ushort>(rented.AsSpan(0, pixels.Width * 8));
            for (int y = 0; y < pixels.Height; y++)
            {
                ToCanonical(pixels, y, canonical, ignoreInvisibleRgb); // x64: ushort memory order is little-endian
                sha.AppendData(rented, 0, pixels.Width * 8);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Peak signal-to-noise ratio in dB over R, G and B (plus A when either image is not fully opaque), with samples
    /// normalised to 0..1 so 8- and 16-bit images share one scale. <see cref="double.PositiveInfinity"/> when identical.
    /// </summary>
    public static double Psnr(PixelBuffer a, PixelBuffer b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException("Images must have the same size.", nameof(b));
        }

        int channels = AlphaOps.IsOpaque(a) && AlphaOps.IsOpaque(b) ? 3 : 4;
        var rowSums = new double[a.Height];
        Parallel.For(0, a.Height, y =>
        {
            ushort[] rented = ArrayPool<ushort>.Shared.Rent(a.Width * 8);
            try
            {
                Span<ushort> ca = rented.AsSpan(0, a.Width * 4), cb = rented.AsSpan(a.Width * 4, a.Width * 4);
                ToCanonical(a, y, ca, ignoreInvisibleRgb: false);
                ToCanonical(b, y, cb, ignoreInvisibleRgb: false);
                double sum = 0;
                for (int i = 0; i < ca.Length; i += 4)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        double d = ca[i + c] - cb[i + c];
                        sum += d * d;
                    }
                }

                rowSums[y] = sum;
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(rented);
            }
        });

        double total = 0;
        foreach (double s in rowSums)
        {
            total += s;
        }

        double mse = total / (65535.0 * 65535.0 * channels * a.PixelCount);
        return mse == 0 ? double.PositiveInfinity : -10 * Math.Log10(mse);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool RowsVisiblyEqual(PixelBuffer a, PixelBuffer b, int y)
    {
        if (a.Depth == SampleDepth.Eight)
        {
            ReadOnlySpan<Rgba32> ra = a.Row32(y), rb = b.Row32(y);
            for (int x = 0; x < ra.Length; x++)
            {
                if (ra[x] != rb[x] && (ra[x].A != 0 || rb[x].A != 0))
                {
                    return false;
                }
            }

            return true;
        }

        ReadOnlySpan<Rgba64> sa = a.Row64(y), sb = b.Row64(y);
        for (int x = 0; x < sa.Length; x++)
        {
            if (sa[x] != sb[x] && (sa[x].A != 0 || sb[x].A != 0))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool RowsVisiblyEqualMixed(PixelBuffer a, PixelBuffer b, int y, bool ignoreInvisibleRgb)
    {
        PixelBuffer eight = a.Depth == SampleDepth.Eight ? a : b, sixteen = ReferenceEquals(eight, a) ? b : a;
        ReadOnlySpan<Rgba32> r8 = eight.Row32(y);
        ReadOnlySpan<Rgba64> r16 = sixteen.Row64(y);
        for (int x = 0; x < r8.Length; x++)
        {
            Rgba32 p = r8[x];
            Rgba64 q = r16[x];
            if (p.A * 257 != q.A)
            {
                return false;
            }

            if ((p.A != 0 || !ignoreInvisibleRgb) && (p.R * 257 != q.R || p.G * 257 != q.G || p.B * 257 != q.B))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes row <paramref name="y"/> as canonical R, G, B, A 16-bit samples (8-bit values × 257).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ToCanonical(PixelBuffer px, int y, Span<ushort> dst, bool ignoreInvisibleRgb)
    {
        if (px.Depth == SampleDepth.Eight)
        {
            ReadOnlySpan<byte> row = px.RowBytes(y);
            for (int i = 0; i < row.Length; i++)
            {
                dst[i] = (ushort)(row[i] * 257);
            }
        }
        else
        {
            MemoryMarshal.Cast<byte, ushort>(px.RowBytes(y)).CopyTo(dst);
        }

        if (ignoreInvisibleRgb)
        {
            for (int i = 0; i < dst.Length; i += 4)
            {
                if (dst[i + 3] == 0)
                {
                    dst[i] = dst[i + 1] = dst[i + 2] = 0;
                }
            }
        }
    }
}
