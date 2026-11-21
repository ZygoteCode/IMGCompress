using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Imaging;

public class ResamplerTests
{
    public static TheoryData<ResampleFilter> Filters => [ResampleFilter.Lanczos3, ResampleFilter.Mitchell, ResampleFilter.Box];

    [Theory]
    [MemberData(nameof(Filters))]
    public void Resize_ConstantImageStaysConstant(ResampleFilter filter)
    {
        foreach (var color in new[] { new Rgba32(200, 100, 50, 255), new Rgba32(10, 200, 90, 128), new Rgba32(1, 2, 3, 255) })
        {
            using var px = Fill(97, 61, color);
            foreach (var (w, h) in new[] { (40, 23), (150, 101), (97, 30), (1, 1) })
            {
                using var r = Resampler.Resize(px, w, h, filter);
                Assert.Equal((w, h, SampleDepth.Eight), (r.Width, r.Height, r.Depth));
                for (int y = 0; y < h; y++)
                {
                    Assert.All(r.Row32(y).ToArray(), p => Assert.Equal(color, p));
                }
            }
        }

        using var eight = Fill(33, 20, new Rgba32(77, 160, 240, 255));
        using var deep = eight.ToDepth(SampleDepth.Sixteen);
        using var rd = Resampler.Resize(deep, 17, 31, filter);
        Assert.Equal(SampleDepth.Sixteen, rd.Depth);
        for (int y = 0; y < rd.Height; y++)
        {
            Assert.All(rd.Row64(y).ToArray(), p => Assert.Equal(new Rgba64(77 * 257, 160 * 257, 240 * 257, 65535), p));
        }
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public void Resize_AlphaEdgesNoDarkHalo(ResampleFilter filter)
    {
        // Opaque white disc on fully transparent *black*: without premultiplication the black would bleed into the edge.
        using var px = PixelBuffer.Create(120, 90, SampleDepth.Eight);
        for (int y = 0; y < px.Height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < px.Width; x++)
            {
                double dx = x - 60, dy = y - 45;
                row[x] = (dx * dx) + (dy * dy) < 30 * 30 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 0);
            }
        }

        foreach (var (w, h) in new[] { (60, 45), (37, 29), (200, 150) })
        {
            using var r = Resampler.Resize(px, w, h, filter);
            int partial = 0;
            for (int y = 0; y < h; y++)
            {
                foreach (var p in r.Row32(y))
                {
                    if (p.A == 0)
                    {
                        Assert.Equal(default, p);
                        continue;
                    }

                    partial += p.A < 255 ? 1 : 0;
                    Assert.True(p.R >= 254 && p.G >= 254 && p.B >= 254, $"dark fringe {p} at {w}x{h} ({filter})");
                }
            }

            if (filter != ResampleFilter.Box || w < px.Width)
            {
                Assert.True(partial > 0, "the edge should be antialiased");
            }
        }
    }

    [Fact]
    public void Resize_AveragesInLinearLight()
    {
        // A 1-pixel checkerboard of black and white averages to 50 % linear light = sRGB 188, not 128.
        using var px = PixelBuffer.Create(64, 64, SampleDepth.Eight);
        for (int y = 0; y < 64; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)(((x + y) & 1) * 255);
                row[x] = new Rgba32(v, v, v);
            }
        }

        using var r = Resampler.Resize(px, 32, 32, ResampleFilter.Box);
        Assert.All(r.Row32(10).ToArray(), p => Assert.Equal(188, p.G));
    }

    [Fact]
    public void Resize_SameSizeIsACopy()
    {
        using var px = TestImages.PhotoLike(50, 40);
        using var r = Resampler.Resize(px, 50, 40, ResampleFilter.Mitchell);
        Assert.False(ReferenceEquals(px, r));
        Assert.True(px.AsSpan().SequenceEqual(r.AsSpan()));
    }

    [Fact]
    public void FitWithin_NeverUpscales()
    {
        Assert.Equal((100, 50), Resampler.FitWithin(100, 50, 200, 200));
        Assert.Equal((100, 50), Resampler.FitWithin(100, 50, 100, 50));
        Assert.Equal((1440, 1080), Resampler.FitWithin(4000, 3000, 1920, 1080));
        Assert.Equal((1440, 1920), Resampler.FitWithin(3000, 4000, 1920, 1920));
        Assert.Equal((1920, 1080), Resampler.FitWithin(3840, 2160, 1920, 1080));
        Assert.Equal((1, 5), Resampler.FitWithin(10, 1000, 100, 5));
        Assert.Equal((1000, 750), Resampler.FitWithin(4000, 3000, 1000, 0));
        Assert.Equal((400, 300), Resampler.FitWithin(4000, 3000, 0, 300));
        Assert.Equal((4000, 3000), Resampler.FitWithin(4000, 3000, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.FitWithin(0, 10, 5, 5));
    }

    private static PixelBuffer Fill(int w, int h, Rgba32 color)
    {
        var px = PixelBuffer.Create(w, h, SampleDepth.Eight);
        for (int y = 0; y < h; y++)
        {
            px.Row32(y).Fill(color);
        }

        return px;
    }
}
