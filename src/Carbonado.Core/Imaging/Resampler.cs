using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Carbonado.Core.Imaging;

/// <summary>
/// Separable image resampling in linear light with premultiplied alpha, so edges against transparency get no dark
/// fringes and bright detail keeps its energy. Rows are processed in parallel.
/// </summary>
public static unsafe class Resampler
{
    /// <summary>
    /// Resizes <paramref name="src"/> to <paramref name="width"/> × <paramref name="height"/> and returns a new buffer of
    /// the same depth (a copy when the size is unchanged). Pixels are treated as sRGB; filter weights are renormalised
    /// at the borders and results are clamped, so a constant image stays exactly constant.
    /// </summary>
    public static PixelBuffer Resize(PixelBuffer src, int width, int height, ResampleFilter filter = ResampleFilter.Lanczos3)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == src.Width && height == src.Height)
        {
            return src.Clone();
        }

        var columns = Taps.Create(src.Width, width, filter);
        var rows = Taps.Create(src.Height, height, filter);
        long rowFloats = (long)width * 4;
        float* inter = (float*)NativeMemory.AlignedAlloc((nuint)(rowFloats * src.Height * sizeof(float)), 64);
        var dst = PixelBuffer.Create(width, height, src.Depth, clear: false);
        try
        {
            Parallel.For(0, src.Height, y => HorizontalPass(src, y, columns, inter + (y * rowFloats)));
            Parallel.For(0, height, y => VerticalPass(inter, rowFloats, rows, y, dst));
        }
        catch
        {
            dst.Dispose();
            throw;
        }
        finally
        {
            NativeMemory.AlignedFree(inter);
        }

        return dst;
    }

    /// <summary>
    /// The largest size with the aspect ratio of <paramref name="w"/> × <paramref name="h"/> that fits within
    /// <paramref name="maxW"/> × <paramref name="maxH"/>; never larger than the input and never below 1×1.
    /// A non-positive limit leaves that dimension unconstrained.
    /// </summary>
    public static (int W, int H) FitWithin(int w, int h, int maxW, int maxH)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(w);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(h);
        double scale = 1;
        if (maxW > 0 && w > maxW)
        {
            scale = maxW / (double)w;
        }

        if (maxH > 0 && h > maxH)
        {
            scale = Math.Min(scale, maxH / (double)h);
        }

        if (scale >= 1)
        {
            return (w, h);
        }

        int fw = Math.Max(1, (int)Math.Round(w * scale)), fh = Math.Max(1, (int)Math.Round(h * scale));
        return (maxW > 0 ? Math.Min(fw, maxW) : fw, maxH > 0 ? Math.Min(fh, maxH) : fh);
    }

    /// <summary>Source row → linear premultiplied RGBA → filtered along x into the intermediate row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void HorizontalPass(PixelBuffer src, int y, Taps taps, float* output)
    {
        int n = src.Width * 4;
        float[] rented = ArrayPool<float>.Shared.Rent(n);
        try
        {
            Span<float> line = rented.AsSpan(0, n);
            ToLinearPremultiplied(src, y, line);
            ref float l = ref MemoryMarshal.GetReference(line);
            for (int x = 0; x < taps.Count; x++)
            {
                int start = taps.Start[x];
                ReadOnlySpan<float> w = taps.WeightsOf(x);
                var acc = Vector128<float>.Zero;
                for (int k = 0; k < w.Length; k++)
                {
                    acc = Vector128.MultiplyAddEstimate(Vector128.Create(w[k]), Vector128.LoadUnsafe(ref l, (nuint)((start + k) * 4)), acc);
                }

                acc.Store(output + (x * 4));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>Intermediate rows → filtered along y → unpremultiplied, sRGB-encoded destination row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void VerticalPass(float* inter, long rowFloats, Taps taps, int y, PixelBuffer dst)
    {
        int n = (int)rowFloats;
        float[] rented = ArrayPool<float>.Shared.Rent(n);
        try
        {
            Span<float> acc = rented.AsSpan(0, n);
            acc.Clear();
            ref float a = ref MemoryMarshal.GetReference(acc);
            int start = taps.Start[y];
            ReadOnlySpan<float> w = taps.WeightsOf(y);
            for (int k = 0; k < w.Length; k++)
            {
                float* row = inter + ((start + k) * rowFloats);
                var wk = Vector256.Create(w[k]);
                int i = 0;
                for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    Vector256.MultiplyAddEstimate(wk, Vector256.Load(row + i), Vector256.LoadUnsafe(ref a, (nuint)i)).StoreUnsafe(ref a, (nuint)i);
                }

                for (; i < n; i++)
                {
                    acc[i] += w[k] * row[i];
                }
            }

            FromLinearPremultiplied(acc, dst, y);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ToLinearPremultiplied(PixelBuffer src, int y, Span<float> line)
    {
        if (src.Depth == SampleDepth.Eight)
        {
            ReadOnlySpan<float> lut = ColorSpace.SrgbToLinearLut8;
            ReadOnlySpan<Rgba32> row = src.Row32(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 p = row[x];
                float a = p.A * (1f / 255);
                line[(x * 4) + 0] = lut[p.R] * a;
                line[(x * 4) + 1] = lut[p.G] * a;
                line[(x * 4) + 2] = lut[p.B] * a;
                line[(x * 4) + 3] = a;
            }
        }
        else
        {
            ReadOnlySpan<float> lut = Decode16.Table;
            ReadOnlySpan<Rgba64> row = src.Row64(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba64 p = row[x];
                float a = p.A * (1f / 65535);
                line[(x * 4) + 0] = lut[p.R] * a;
                line[(x * 4) + 1] = lut[p.G] * a;
                line[(x * 4) + 2] = lut[p.B] * a;
                line[(x * 4) + 3] = a;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FromLinearPremultiplied(ReadOnlySpan<float> acc, PixelBuffer dst, int y)
    {
        if (dst.Depth == SampleDepth.Eight)
        {
            ReadOnlySpan<byte> encode = Encode8.Table;
            Span<Rgba32> row = dst.Row32(y);
            for (int x = 0; x < row.Length; x++)
            {
                float alpha = Math.Clamp(acc[(x * 4) + 3], 0f, 1f);
                byte a8 = (byte)((alpha * 255) + 0.5f);
                if (a8 == 0)
                {
                    row[x] = default;
                    continue;
                }

                float inv = alpha < 1 ? 1 / alpha : 1;
                row[x] = new Rgba32(
                    encode[ToIndex16(acc[x * 4], alpha, inv)],
                    encode[ToIndex16(acc[(x * 4) + 1], alpha, inv)],
                    encode[ToIndex16(acc[(x * 4) + 2], alpha, inv)],
                    a8);
            }
        }
        else
        {
            Span<Rgba64> row = dst.Row64(y);
            for (int x = 0; x < row.Length; x++)
            {
                float alpha = Math.Clamp(acc[(x * 4) + 3], 0f, 1f);
                ushort a16 = (ushort)((alpha * 65535) + 0.5f);
                if (a16 == 0)
                {
                    row[x] = default;
                    continue;
                }

                float inv = alpha < 1 ? 1 / alpha : 1;
                row[x] = new Rgba64(
                    Encode16(acc[x * 4], alpha, inv),
                    Encode16(acc[(x * 4) + 1], alpha, inv),
                    Encode16(acc[(x * 4) + 2], alpha, inv),
                    a16);
            }
        }
    }

    /// <summary>Unpremultiplied linear value quantised to 0..65535 (index into <see cref="Encode8.Table"/>).</summary>
    private static int ToIndex16(float premultiplied, float alpha, float inv) =>
        (int)((Math.Clamp(premultiplied, 0f, alpha) * inv * 65535) + 0.5f);

    private static ushort Encode16(float premultiplied, float alpha, float inv) =>
        (ushort)((ColorSpace.LinearToSrgbPrecise(Math.Min(Math.Clamp(premultiplied, 0f, alpha) * inv, 1f)) * 65535) + 0.5);

    private static double Kernel(ResampleFilter filter, double x)
    {
        x = Math.Abs(x);
        switch (filter)
        {
            case ResampleFilter.Box:
                return x <= 0.5 ? 1 : 0;
            case ResampleFilter.Mitchell:
                // Mitchell–Netravali with B = C = 1/3, as piecewise cubics in Horner form (coefficients already / 6).
                const double B = 1.0 / 3, C = 1.0 / 3;
                const double P3 = (12 - (9 * B) - (6 * C)) / 6, P2 = (-18 + (12 * B) + (6 * C)) / 6, P0 = (6 - (2 * B)) / 6;
                const double Q3 = (-B - (6 * C)) / 6, Q2 = ((6 * B) + (30 * C)) / 6, Q1 = ((-12 * B) - (48 * C)) / 6, Q0 = ((8 * B) + (24 * C)) / 6;
                if (x < 1)
                {
                    return (((P3 * x) + P2) * x * x) + P0;
                }

                return x < 2 ? (((((Q3 * x) + Q2) * x) + Q1) * x) + Q0 : 0;
            default:
                return x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
        }
    }

    private static double Sinc(double x) => x < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Radius(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Box => 0.5,
        ResampleFilter.Mitchell => 2,
        _ => 3,
    };

    /// <summary>Per output sample: the first source index and normalised weights of its contributing source samples.</summary>
    private sealed class Taps
    {
        private readonly float[] _weights;
        private readonly int[] _length;
        private readonly int _stride;

        private Taps(int count, int stride)
        {
            Start = new int[count];
            _length = new int[count];
            _weights = new float[count * stride];
            _stride = stride;
        }

        public int[] Start { get; }

        public int Count => Start.Length;

        public ReadOnlySpan<float> WeightsOf(int i) => _weights.AsSpan(i * _stride, _length[i]);

        public static Taps Create(int srcLength, int dstLength, ResampleFilter filter)
        {
            double scale = srcLength / (double)dstLength;
            double filterScale = Math.Max(1, scale);
            double support = Radius(filter) * filterScale;
            int stride = Math.Min(srcLength, (int)Math.Ceiling(support * 2) + 1);
            var taps = new Taps(dstLength, stride);
            double[] buffer = new double[stride];
            for (int i = 0; i < dstLength; i++)
            {
                double center = ((i + 0.5) * scale) - 0.5;
                int first = Math.Max(0, (int)Math.Ceiling(center - support));
                int last = Math.Min(srcLength - 1, (int)Math.Floor(center + support));
                int count = Math.Min(stride, last - first + 1);
                double sum = 0;
                for (int k = 0; k < count; k++)
                {
                    buffer[k] = Kernel(filter, (first + k - center) / filterScale);
                    sum += buffer[k];
                }

                if (Math.Abs(sum) < 1e-12)
                {
                    // Degenerate window (cannot happen with these kernels, kept as a safe fallback): nearest sample.
                    first = Math.Clamp((int)Math.Round(center), 0, srcLength - 1);
                    count = 1;
                    buffer[0] = sum = 1;
                }

                taps.Start[i] = first;
                taps._length[i] = count;
                for (int k = 0; k < count; k++)
                {
                    taps._weights[(i * stride) + k] = (float)(buffer[k] / sum);
                }
            }

            return taps;
        }
    }

    /// <summary>Linear value of every 16-bit sRGB code (built on first use).</summary>
    private static class Decode16
    {
        public static readonly float[] Table = Build();

        private static float[] Build()
        {
            var lut = new float[65536];
            for (int i = 0; i < lut.Length; i++)
            {
                lut[i] = (float)ColorSpace.SrgbToLinearPrecise(i / 65535.0);
            }

            return lut;
        }
    }

    /// <summary>8-bit sRGB code of linear values quantised to 0..65535, max error 0.03 code values (built on first use).</summary>
    private static class Encode8
    {
        public static readonly byte[] Table = Build();

        private static byte[] Build()
        {
            var lut = new byte[65536];
            for (int i = 0; i < lut.Length; i++)
            {
                lut[i] = (byte)((ColorSpace.LinearToSrgbPrecise(i / 65535.0) * 255) + 0.5);
            }

            return lut;
        }
    }
}
