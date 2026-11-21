using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Analysis;

public class PixelComparerTests
{
    [Fact]
    public void Identical_IgnoresInvisibleRgbWhenAsked()
    {
        using var a = TestImages.WithAlpha(64, 48);
        using var b = a.Clone();
        Assert.True(PixelComparer.Identical(a, b, ignoreInvisibleRgb: false));

        b.Row32(0)[3] = new Rgba32(250, 1, 2, 0); // row 0 is fully transparent
        Assert.False(PixelComparer.Identical(a, b, ignoreInvisibleRgb: false));
        Assert.True(PixelComparer.Identical(a, b, ignoreInvisibleRgb: true));

        // Across depths through the canonical 16-bit form.
        using var b16 = b.ToDepth(SampleDepth.Sixteen);
        Assert.True(PixelComparer.Identical(a, b16, ignoreInvisibleRgb: true));
        Assert.False(PixelComparer.Identical(a, b16, ignoreInvisibleRgb: false));
        b16.Row64(20)[20].R ^= 1; // a 16-bit-only difference is a difference
        Assert.False(PixelComparer.Identical(a, b16, ignoreInvisibleRgb: true));

        // A visible change, or an alpha change, always counts.
        using var c = a.Clone();
        c.Row32(40)[60].G ^= 1;
        Assert.False(PixelComparer.Identical(a, c, ignoreInvisibleRgb: true));
        using var d = a.Clone();
        d.Row32(0)[3].A = 1;
        Assert.False(PixelComparer.Identical(a, d, ignoreInvisibleRgb: true));

        using var other = TestImages.WithAlpha(64, 47);
        Assert.False(PixelComparer.Identical(a, other, ignoreInvisibleRgb: true));
    }

    [Fact]
    public void Hash_EqualForEqualVisiblePixels()
    {
        using var a = TestImages.WithAlpha(64, 48);
        using var b = a.Clone();
        b.Row32(1)[9] = new Rgba32(7, 7, 7, 0);
        byte[] ha = PixelComparer.Hash(a, ignoreInvisibleRgb: true);
        Assert.Equal(32, ha.Length);
        Assert.Equal(ha, PixelComparer.Hash(b, ignoreInvisibleRgb: true));
        Assert.NotEqual(PixelComparer.Hash(a, ignoreInvisibleRgb: false), PixelComparer.Hash(b, ignoreInvisibleRgb: false));

        using var a16 = a.ToDepth(SampleDepth.Sixteen);
        Assert.Equal(PixelComparer.Hash(a, ignoreInvisibleRgb: false), PixelComparer.Hash(a16, ignoreInvisibleRgb: false));

        // Same bytes, different shape: different hash.
        using var wide = PixelBuffer.Create(8, 2, SampleDepth.Eight);
        using var tall = PixelBuffer.Create(4, 4, SampleDepth.Eight);
        Assert.NotEqual(PixelComparer.Hash(wide, false), PixelComparer.Hash(tall, false));

        using var c = a.Clone();
        c.Row32(30)[30].B ^= 1;
        Assert.NotEqual(ha, PixelComparer.Hash(c, ignoreInvisibleRgb: true));
    }

    [Fact]
    public void Psnr_KnownNoise()
    {
        using var a = TestImages.Gradient(64, 48);
        for (int y = 0; y < a.Height; y++)
        {
            var row = a.Row32(y);
            for (int x = 0; x < a.Width; x++)
            {
                row[x].R = (byte)Math.Min(row[x].R, (byte)200);
            }
        }

        Assert.Equal(double.PositiveInfinity, PixelComparer.Psnr(a, a));

        // +5 on R only, opaque: MSE over RGB = 25 / 3 (in 8-bit units).
        using var b = a.Clone();
        for (int y = 0; y < b.Height; y++)
        {
            var row = b.Row32(y);
            for (int x = 0; x < b.Width; x++)
            {
                row[x].R += 5;
            }
        }

        double expected = 10 * Math.Log10(255.0 * 255.0 / (25.0 / 3));
        Assert.Equal(expected, PixelComparer.Psnr(a, b), 9);
        using var b16 = b.ToDepth(SampleDepth.Sixteen);
        Assert.Equal(expected, PixelComparer.Psnr(a, b16), 9);

        // With transparency the alpha channel takes part: same error spread over 4 channels.
        using var at = a.Clone();
        using var bt = b.Clone();
        at.Row32(0)[0].A = bt.Row32(0)[0].A = 128;
        Assert.Equal(10 * Math.Log10(255.0 * 255.0 / (25.0 / 4)), PixelComparer.Psnr(at, bt), 9);

        using var small = TestImages.Gradient(10, 10);
        Assert.Throws<ArgumentException>(() => PixelComparer.Psnr(a, small));
    }
}
