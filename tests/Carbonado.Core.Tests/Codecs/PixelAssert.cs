using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>Pixel comparisons for codec tests (local helpers; independent of the analysis workstream).</summary>
public static class PixelAssert
{
    /// <summary>Same size, same depth, byte-identical samples.</summary>
    public static void Identical(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Depth, actual.Depth);
        for (int y = 0; y < expected.Height; y++)
        {
            var e = expected.RowBytes(y);
            var a = actual.RowBytes(y);
            if (!e.SequenceEqual(a))
            {
                int i = e.CommonPrefixLength(a) / expected.BytesPerPixel;
                Assert.Fail($"Pixel ({i},{y}) differs: expected {Describe(expected, i, y)}, actual {Describe(actual, i, y)}.");
            }
        }
    }

    /// <summary>
    /// Same size and identical visible pixels at 16-bit precision (8-bit values widened by ×257); RGB of fully
    /// transparent pixels is ignored.
    /// </summary>
    public static void VisiblyIdentical(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba64 e = At(expected, x, y), a = At(actual, x, y);
                bool same = e.A == 0 ? a.A == 0 : e == a;
                if (!same)
                {
                    Assert.Fail($"Pixel ({x},{y}) differs: expected {e}, actual {a}.");
                }
            }
        }
    }

    /// <summary>Mean per-sample absolute difference at 8-bit precision.</summary>
    public static double MeanDifference8(PixelBuffer a, PixelBuffer b)
    {
        using var a8 = a.Depth == SampleDepth.Eight ? a.Clone() : a.ToDepth(SampleDepth.Eight);
        using var b8 = b.Depth == SampleDepth.Eight ? b.Clone() : b.ToDepth(SampleDepth.Eight);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long total = 0;
        for (int y = 0; y < a.Height; y++)
        {
            var ra = a8.RowBytes(y);
            var rb = b8.RowBytes(y);
            for (int i = 0; i < ra.Length; i++)
            {
                total += Math.Abs(ra[i] - rb[i]);
            }
        }

        return (double)total / (a.PixelCount * 4);
    }

    public static Rgba64 At(PixelBuffer px, int x, int y)
    {
        if (px.Depth == SampleDepth.Sixteen)
        {
            return px.Row64(y)[x];
        }

        Rgba32 p = px.Row32(y)[x];
        return new Rgba64((ushort)(p.R * 257), (ushort)(p.G * 257), (ushort)(p.B * 257), (ushort)(p.A * 257));
    }

    private static string Describe(PixelBuffer px, int x, int y) =>
        px.Depth == SampleDepth.Eight ? px.Row32(y)[x].ToString() : px.Row64(y)[x].ToString();
}
