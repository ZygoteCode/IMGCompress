using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Imaging;

public class AlphaOpsTests
{
    [Fact]
    public void IsOpaque_DetectsAnyNonOpaquePixel()
    {
        using var px = TestImages.Gradient(67, 9);
        Assert.True(AlphaOps.IsOpaque(px));
        using var deep = px.ToDepth(SampleDepth.Sixteen);
        Assert.True(AlphaOps.IsOpaque(deep));

        px.Row32(8)[66].A = 254; // last pixel: exercises the scalar tail after the vector loop
        Assert.False(AlphaOps.IsOpaque(px));
        deep.Row64(3)[10].A = 65534;
        Assert.False(AlphaOps.IsOpaque(deep));
    }

    [Fact]
    public void CleanInvisible_ZeroesRgbOnlyWhereAlphaIsZero()
    {
        using var px = TestImages.WithAlpha(41, 30);
        px.Row32(0)[40] = new Rgba32(9, 8, 7, 0);
        using var before = px.Clone();
        AlphaOps.CleanInvisible(px);
        for (int y = 0; y < px.Height; y++)
        {
            for (int x = 0; x < px.Width; x++)
            {
                Rgba32 b = before.Row32(y)[x], a = px.Row32(y)[x];
                Assert.Equal(b.A == 0 ? new Rgba32(0, 0, 0, 0) : b, a);
            }
        }

        using var deep = before.ToDepth(SampleDepth.Sixteen);
        AlphaOps.CleanInvisible(deep);
        Assert.Equal(new Rgba64(0, 0, 0, 0), deep.Row64(0)[40]);
        Assert.Equal(before.Row32(29)[40].R * 257, deep.Row64(29)[40].R);
    }

    [Fact]
    public void Flatten_CompositesOverBackgroundAndIsOpaque()
    {
        using var px = PixelBuffer.Create(4, 1, SampleDepth.Eight);
        var row = px.Row32(0);
        row[0] = new Rgba32(255, 255, 255, 128);
        row[1] = new Rgba32(10, 20, 30, 0);
        row[2] = new Rgba32(10, 20, 30, 255);
        row[3] = new Rgba32(200, 100, 0, 64);
        var bg = new Rgba32(0, 50, 100, 7);
        using var flat = AlphaOps.Flatten(px, bg);
        var f = flat.Row32(0);
        Assert.Equal(new Rgba32(128, 153, 178, 255), f[0]);
        Assert.Equal(new Rgba32(0, 50, 100, 255), f[1]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), f[2]);
        Assert.Equal(new Rgba32(50, 63, 75, 255), f[3]);
        Assert.True(AlphaOps.IsOpaque(flat));

        using var deep = px.ToDepth(SampleDepth.Sixteen);
        using var flatDeep = AlphaOps.Flatten(deep, bg);
        Assert.Equal(SampleDepth.Sixteen, flatDeep.Depth);
        Assert.Equal(65535, flatDeep.Row64(0)[0].A);
        Assert.Equal(128 * 257, flatDeep.Row64(0)[0].R, 1.0);
        Assert.Equal(new Rgba64(0, 50 * 257, 100 * 257, 65535), flatDeep.Row64(0)[1]);
    }
}
