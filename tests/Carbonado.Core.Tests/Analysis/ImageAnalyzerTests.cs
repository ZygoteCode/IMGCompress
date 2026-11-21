using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Analysis;

public class ImageAnalyzerTests
{
    [Fact]
    public void Analyze_ClassifiesSyntheticKinds()
    {
        using var graphic = TestImages.Graphic();
        var g = ImageAnalyzer.Analyze(graphic);
        Assert.Equal(ContentKind.Graphic, g.Kind);
        Assert.InRange(g.UniqueColors, 2, 7);
        Assert.True(g.FitsPalette);
        Assert.True(g.FlatRatio > 0.55);
        Assert.InRange(g.EdgeDensity, 0.001, 0.5);

        // Camera-like content: smooth photo plus independent per-channel sensor noise.
        using var photo = NoisyPhoto(640, 480);
        var p = ImageAnalyzer.Analyze(photo);
        Assert.Equal(ContentKind.Photo, p.Kind);
        Assert.True(p.UniqueColors is -1 or > 20_000);
        Assert.True(p.FlatRatio < 0.35);
        Assert.False(p.FitsPalette);
        Assert.False(p.IsGrayscale);

        // A photo with a flat panel over ~45 % of every row: many colors but a mid flat ratio.
        using var mixed = NoisyPhoto(640, 480);
        for (int y = 0; y < mixed.Height; y++)
        {
            mixed.Row32(y)[..288].Fill(new Rgba32(30, 30, 40));
        }

        var m = ImageAnalyzer.Analyze(mixed);
        Assert.InRange(m.FlatRatio, 0.35, 0.55);
        Assert.Equal(ContentKind.Mixed, m.Kind);

        // Few colors make a graphic even without flat runs (every pixel differs from its left neighbour).
        using var dither = PixelBuffer.Create(96, 64, SampleDepth.Eight);
        for (int y = 0; y < dither.Height; y++)
        {
            for (int x = 0; x < dither.Width; x++)
            {
                int i = ((x * 7) + (y * 13)) % 200;
                dither.Row32(y)[x] = new Rgba32((byte)i, (byte)(255 - i), (byte)(i * 3));
            }
        }

        var d = ImageAnalyzer.Analyze(dither);
        Assert.Equal((0.0, 200, ContentKind.Graphic), (d.FlatRatio, d.UniqueColors, d.Kind));
    }

    [Fact]
    public void Analyze_SmoothHighColorImageIsNotGraphic()
    {
        // Smooth 16-bit gradient with 3-pixel horizontal runs: flat ratio 341/511 ≈ 0.67, 171 × 512 = 87 552 colors.
        using var smooth = SmoothRuns(512, 512);
        var s = ImageAnalyzer.Analyze(smooth);
        Assert.Equal(-1, s.UniqueColors);
        Assert.Equal(341.0 / 511, s.FlatRatio, 12);
        Assert.Equal(ContentKind.Mixed, s.Kind);

        // The same content with an exact color count (171 × 256 = 43 776) is still a graphic through the flat-ratio rule.
        using var counted = SmoothRuns(512, 256);
        var c = ImageAnalyzer.Analyze(counted);
        Assert.Equal((43_776, ContentKind.Graphic), (c.UniqueColors, c.Kind));

        // Real case: the Windows "bloom" wallpaper (> 65 536 colors, flat ratio ≈ 0.58) is no longer called a graphic.
        if (TestImages.SystemPhotos(1) is [var jpeg])
        {
            using var photo = TestImages.LoadWithWic(jpeg);
            var p = ImageAnalyzer.Analyze(photo);
            if (p.UniqueColors == -1)
            {
                Assert.NotEqual(ContentKind.Graphic, p.Kind);
            }
        }
    }

    [Fact]
    public void Analyze_DetectsAlphaUsage()
    {
        using var opaque = TestImages.Gradient(64, 48);
        var o = ImageAnalyzer.Analyze(opaque);
        Assert.Equal(AlphaUsage.Opaque, o.Alpha);
        Assert.False(o.HasAlpha);

        using var binary = opaque.Clone();
        binary.Row32(10)[5] = new Rgba32(1, 2, 3, 0);
        Assert.Equal(AlphaUsage.Binary, ImageAnalyzer.Analyze(binary).Alpha);

        using var translucent = TestImages.WithAlpha(64, 48);
        var t = ImageAnalyzer.Analyze(translucent);
        Assert.Equal(AlphaUsage.Translucent, t.Alpha);
        Assert.True(t.HasAlpha);

        using var deep = binary.ToDepth(SampleDepth.Sixteen);
        Assert.Equal(AlphaUsage.Binary, ImageAnalyzer.Analyze(deep).Alpha);
        deep.Row64(0)[0].A = 65534;
        Assert.Equal(AlphaUsage.Translucent, ImageAnalyzer.Analyze(deep).Alpha);
    }

    [Fact]
    public void Analyze_UniqueColorsCapped()
    {
        // 256 × 256 = exactly 65 536 distinct colors: still counted exactly.
        using var px = PixelBuffer.Create(256, 257, SampleDepth.Eight);
        for (int y = 0; y < 256; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < 256; x++)
            {
                row[x] = new Rgba32((byte)x, (byte)y, 7);
            }
        }

        px.Row32(256).Fill(new Rgba32(0, 0, 7)); // repeats only
        var a = ImageAnalyzer.Analyze(px);
        Assert.Equal(65_536, a.UniqueColors);
        Assert.False(a.FitsPalette);

        px.Row32(256)[100] = new Rgba32(0, 0, 0, 0); // the 65 537th value (also the all-zero pixel)
        Assert.Equal(-1, ImageAnalyzer.Analyze(px).UniqueColors);

        using var pal = TestImages.Palette(colors: 16);
        var p = ImageAnalyzer.Analyze(pal);
        Assert.Equal(16, p.UniqueColors);
        Assert.True(p.FitsPalette);

        // 16-bit: the exact 8-bit expansion has the same colors; real 16-bit detail is never "palette".
        using var pal16 = pal.ToDepth(SampleDepth.Sixteen);
        var p16 = ImageAnalyzer.Analyze(pal16);
        Assert.Equal((16, true, false), (p16.UniqueColors, p16.FitsPalette, p16.Has16BitPrecision));

        using var deep = TestImages.Deep16();
        var d = ImageAnalyzer.Analyze(deep);
        Assert.True(d.Has16BitPrecision);
        Assert.False(d.FitsPalette);
    }

    [Fact]
    public void Analyze_GrayscaleFlatAndEdgeStatistics()
    {
        using var gray = TestImages.Gray();
        Assert.True(ImageAnalyzer.Analyze(gray).IsGrayscale);

        using var flat = PixelBuffer.Create(10, 4, SampleDepth.Eight);
        var f = ImageAnalyzer.Analyze(flat);
        Assert.Equal((1.0, 0.0, 1), (f.FlatRatio, f.EdgeDensity, f.UniqueColors));
        Assert.Equal(ContentKind.Graphic, f.Kind);

        // Vertical stripes 0/255 every pixel: no pixel equals its left neighbour; every pixel is on an edge
        // except the last column (no right neighbour, and no vertical change).
        using var stripes = PixelBuffer.Create(8, 3, SampleDepth.Eight);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                byte v = (byte)((x & 1) * 255);
                stripes.Row32(y)[x] = new Rgba32(v, v, v);
            }
        }

        var s = ImageAnalyzer.Analyze(stripes);
        Assert.Equal(0.0, s.FlatRatio);
        Assert.Equal(7.0 / 8, s.EdgeDensity, 12);

        using var column = PixelBuffer.Create(1, 5, SampleDepth.Eight);
        Assert.Equal(0.0, ImageAnalyzer.Analyze(column).FlatRatio);
    }

    /// <summary>16-bit linear ramps in x (steps every 3 pixels) and y: every (x / 3, y) pair is a distinct color.</summary>
    private static PixelBuffer SmoothRuns(int w, int h)
    {
        var px = PixelBuffer.Create(w, h, SampleDepth.Sixteen);
        for (int y = 0; y < h; y++)
        {
            var row = px.Row64(y);
            for (int x = 0; x < w; x++)
            {
                int q = x / 3;
                row[x] = new Rgba64((ushort)(q * 383), (ushort)(y * 128), (ushort)(20_000 + ((q + y) * 40)));
            }
        }

        return px;
    }

    private static PixelBuffer NoisyPhoto(int w, int h)
    {
        var px = TestImages.PhotoLike(w, h);
        var rng = new Random(3);
        for (int y = 0; y < h; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < w; x++)
            {
                ref var c = ref row[x];
                c = new Rgba32(Noise(c.R, rng), Noise(c.G, rng), Noise(c.B, rng));
            }
        }

        return px;
    }

    private static byte Noise(byte v, Random rng) => (byte)Math.Clamp(v + rng.Next(-4, 5), 0, 255);
}
