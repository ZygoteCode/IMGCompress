using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Carbonado.Core.Codecs;

/// <summary>
/// PNG scanline filter kernels (filter method 0). Rows exclude the leading filter-type byte; the previous row of the
/// first scanline (of each Adam7 pass) is all zeros. <c>bpp</c> is the filter distance: bytes per complete pixel, at least 1.
/// </summary>
internal static class PngFilters
{
    public const int None = 0, Sub = 1, Up = 2, Average = 3, Paeth = 4;

    /// <summary>Reverses filter <paramref name="type"/> in place.</summary>
    /// <exception cref="InvalidDataException">Unknown filter type.</exception>
    public static unsafe void Unfilter(int type, Span<byte> row, ReadOnlySpan<byte> prev, int bpp)
    {
        int n = row.Length;
        fixed (byte* r = row)
        fixed (byte* p = prev)
        {
            switch (type)
            {
                case None:
                    break;

                case Sub:
                    for (int i = bpp; i < n; i++)
                    {
                        r[i] += r[i - bpp];
                    }

                    break;

                case Up:
                    AddInPlace(row, prev);
                    break;

                case Average:
                    for (int i = 0; i < bpp; i++)
                    {
                        r[i] += (byte)(p[i] >> 1);
                    }

                    for (int i = bpp; i < n; i++)
                    {
                        r[i] += (byte)((r[i - bpp] + p[i]) >> 1);
                    }

                    break;

                case Paeth when (bpp is 3 or 4) && n % bpp == 0 && Vector128.IsHardwareAccelerated:
                    UnfilterPaethPixels(r, p, n, bpp);
                    break;

                case Paeth:
                    for (int i = 0; i < bpp; i++)
                    {
                        r[i] += p[i];
                    }

                    for (int i = bpp; i < n; i++)
                    {
                        r[i] += PaethPredictor(r[i - bpp], p[i], p[i - bpp]);
                    }

                    break;

                default:
                    throw new InvalidDataException($"PNG: unknown scanline filter type {type}.");
            }
        }
    }

    /// <summary>Applies filter <paramref name="type"/> to <paramref name="raw"/>, writing the residuals to <paramref name="dst"/>.</summary>
    public static unsafe void Apply(int type, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prev, Span<byte> dst, int bpp)
    {
        int n = raw.Length;
        fixed (byte* r = raw)
        fixed (byte* p = prev)
        fixed (byte* d = dst)
        {
            switch (type)
            {
                case None:
                    raw.CopyTo(dst);
                    break;

                case Sub:
                    raw[..Math.Min(bpp, n)].CopyTo(dst);
                    if (n > bpp)
                    {
                        Subtract(raw[bpp..], raw[..(n - bpp)], dst[bpp..]);
                    }

                    break;

                case Up:
                    Subtract(raw, prev, dst);
                    break;

                case Average:
                    for (int i = 0; i < Math.Min(bpp, n); i++)
                    {
                        d[i] = (byte)(r[i] - (p[i] >> 1));
                    }

                    for (int i = bpp; i < n; i++)
                    {
                        d[i] = (byte)(r[i] - ((r[i - bpp] + p[i]) >> 1));
                    }

                    break;

                case Paeth:
                    for (int i = 0; i < Math.Min(bpp, n); i++)
                    {
                        d[i] = (byte)(r[i] - p[i]);
                    }

                    for (int i = bpp; i < n; i++)
                    {
                        d[i] = (byte)(r[i] - PaethPredictor(r[i - bpp], p[i], p[i - bpp]));
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }
    }

    /// <summary>Sum of the residuals read as signed bytes, in absolute value (the MinSum heuristic's cost).</summary>
    public static long AbsSum(ReadOnlySpan<byte> residuals)
    {
        long total = 0;
        int i = 0;
        ref byte start = ref MemoryMarshal.GetReference(residuals);
        if (Sse2.IsSupported && Ssse3.IsSupported)
        {
            var acc = Vector128<ulong>.Zero;
            for (; i + 16 <= residuals.Length; i += 16)
            {
                var v = Vector128.LoadUnsafe(ref start, (nuint)i).AsSByte();
                var abs = Ssse3.Abs(v); // |−128| = 128 when read unsigned
                acc += Sse2.SumAbsoluteDifferences(abs, Vector128<byte>.Zero).AsUInt64();
            }

            total = (long)(acc.GetElement(0) + acc.GetElement(1));
        }

        for (; i < residuals.Length; i++)
        {
            int v = (sbyte)Unsafe.Add(ref start, i);
            total += v < 0 ? -v : v;
        }

        return total;
    }

    /// <summary>Shannon entropy (bits) of the residual bytes; <paramref name="histogram"/> is 256 entries of scratch.</summary>
    public static double EntropyBits(ReadOnlySpan<byte> residuals, Span<int> histogram)
    {
        histogram.Clear();
        foreach (byte b in residuals)
        {
            histogram[b]++;
        }

        double n = residuals.Length, sum = 0;
        foreach (int c in histogram)
        {
            if (c > 0)
            {
                sum += c * Math.Log2(c);
            }
        }

        return (n * Math.Log2(Math.Max(n, 1))) - sum;
    }

    /// <summary>
    /// Paeth reversal one RGB/RGBA pixel at a time with 16-bit vector lanes (branchless; the libpng SSE2 approach):
    /// the dependency is per pixel instead of per byte.
    /// </summary>
    private static unsafe void UnfilterPaethPixels(byte* r, byte* p, int n, int bpp)
    {
        Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
        for (int i = 0; i < n; i += bpp)
        {
            Vector128<short> b = Widen(Load(p + i, bpp));
            Vector128<short> pa = b - c, pb = a - c;
            Vector128<short> pc = Vector128.Abs(pa + pb);
            pa = Vector128.Abs(pa);
            pb = Vector128.Abs(pb);
            Vector128<short> smallest = Vector128.Min(pc, Vector128.Min(pa, pb));
            Vector128<short> nearest = Vector128.ConditionalSelect(Vector128.Equals(pa, smallest), a, Vector128.ConditionalSelect(Vector128.Equals(pb, smallest), b, c));
            Vector128<byte> d = Vector128.Narrow(nearest, nearest).AsByte() + Load(r + i, bpp);
            Store(r + i, d, bpp);
            a = Widen(d);
            c = b;
        }

        static Vector128<byte> Load(byte* source, int count) => Vector128.CreateScalarUnsafe(
            count == 4 ? Unsafe.ReadUnaligned<uint>(source) : (uint)(source[0] | (source[1] << 8) | (source[2] << 16))).AsByte();

        static Vector128<short> Widen(Vector128<byte> v) => Vector128.WidenLower(v).AsInt16();

        static void Store(byte* target, Vector128<byte> v, int count)
        {
            uint value = v.AsUInt32().ToScalar();
            if (count == 4)
            {
                Unsafe.WriteUnaligned(target, value);
            }
            else
            {
                target[0] = (byte)value;
                target[1] = (byte)(value >> 8);
                target[2] = (byte)(value >> 16);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PaethPredictor(int a, int b, int c)
    {
        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - c - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static void AddInPlace(Span<byte> row, ReadOnlySpan<byte> prev)
    {
        int i = 0;
        ref byte r = ref MemoryMarshal.GetReference(row);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= row.Length; i += 32)
            {
                (Vector256.LoadUnsafe(ref r, (nuint)i) + Vector256.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
            }
        }

        for (; i + 16 <= row.Length; i += 16)
        {
            (Vector128.LoadUnsafe(ref r, (nuint)i) + Vector128.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
        }

        for (; i < row.Length; i++)
        {
            Unsafe.Add(ref r, i) += Unsafe.Add(ref p, i);
        }
    }

    private static void Subtract(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0;
        ref byte ra = ref MemoryMarshal.GetReference(a);
        ref byte rb = ref MemoryMarshal.GetReference(b);
        ref byte rd = ref MemoryMarshal.GetReference(dst);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= a.Length; i += 32)
            {
                (Vector256.LoadUnsafe(ref ra, (nuint)i) - Vector256.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
            }
        }

        for (; i + 16 <= a.Length; i += 16)
        {
            (Vector128.LoadUnsafe(ref ra, (nuint)i) - Vector128.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }

        for (; i < a.Length; i++)
        {
            Unsafe.Add(ref rd, i) = (byte)(Unsafe.Add(ref ra, i) - Unsafe.Add(ref rb, i));
        }
    }
}
