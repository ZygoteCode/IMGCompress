using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Codecs;

public class PngOptimizerTests
{
    public static TheoryData<string, bool> Images()
    {
        var data = new TheoryData<string, bool>();
        foreach (string name in ImageNames)
        {
            data.Add(name, true);
            data.Add(name, false);
        }

        return data;
    }

    private static readonly string[] ImageNames = ["gradient", "photo", "graphic", "alpha", "palette16", "palette256", "gray", "deep16", "exact16", "binaryAlpha"];

    [Theory]
    [MemberData(nameof(Images))]
    public void Optimize_IsPixelExact(string name, bool cleanInvisible)
    {
        using var px = Make(name);
        foreach (int effort in new[] { 1, 2, 3 })
        {
            byte[] png = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(CleanInvisiblePixels: cleanInvisible, Effort: effort));
            using var decoded = PngTestFiles.Decode(new MemoryStream(png));
            if (cleanInvisible)
            {
                PixelAssert.VisiblyIdentical(px, decoded.Pixels);
            }
            else
            {
                SameValues(px, decoded.Pixels);
            }
        }
    }

    [Fact]
    public void Optimize_ChoosesPaletteFor256Colors()
    {
        using var px = DistinctColors(256);
        byte[] png = PngOptimizer.Optimize(px, null, new PngOptimizeOptions());
        var ihdr = PngCodecTests.ReadIhdr(png);
        Assert.Equal((byte)PngColorType.Palette, ihdr.ColorType);
        Assert.Equal(8, ihdr.BitDepth);
        Assert.Equal(256 * 3, PngTestFiles.Chunks(png).Single(c => c.Type == "PLTE").Length);

        // Few colours pack below 8 bits.
        using var four = TestImages.Palette(64, 32, 4);
        Assert.Equal(((byte)3, (byte)2), Type(PngOptimizer.Optimize(four, null, new PngOptimizeOptions())));

        // 257 colours cannot use a palette.
        using var many = DistinctColors(256);
        many.Row32(0)[0] = new Rgba32(1, 2, 3, 200);
        Assert.NotEqual((byte)PngColorType.Palette, PngCodecTests.ReadIhdr(PngOptimizer.Optimize(many, null, new PngOptimizeOptions())).ColorType);
    }

    [Fact]
    public void Optimize_PaletteWithTransparency_PutsTranslucentEntriesFirst()
    {
        using var px = DistinctColors(40);
        for (int x = 0; x < 8; x++)
        {
            px.Row32(1)[x] = new Rgba32(9, 9, 9, (byte)(x * 30));
        }

        byte[] png = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(CleanInvisiblePixels: false));
        Assert.Equal((byte)PngColorType.Palette, PngCodecTests.ReadIhdr(png).ColorType);
        Assert.Equal(8, PngTestFiles.Chunks(png).Single(c => c.Type == "tRNS").Length);
        using var decoded = PngTestFiles.Decode(new MemoryStream(png));
        PixelAssert.Identical(px, decoded.Pixels);
    }

    [Fact]
    public void Optimize_Gray_UsesGrayType()
    {
        using var gray = TestImages.Gray();
        Assert.Equal((byte)PngColorType.Gray, PngCodecTests.ReadIhdr(PngOptimizer.Optimize(gray, null, new PngOptimizeOptions())).ColorType);

        // Black/white → 1-bit gray.
        using var bw = PixelBuffer.Create(40, 20, SampleDepth.Eight);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                byte v = (byte)(((x / 3) + y) % 2 == 0 ? 0 : 255);
                bw.Row32(y)[x] = new Rgba32(v, v, v);
            }
        }

        Assert.Equal(((byte)0, (byte)1), Type(PngOptimizer.Optimize(bw, null, new PngOptimizeOptions())));

        // An RGB profile that must be kept forbids gray; without the profile gray is used again.
        var rgbIcc = new ImageMetadata { Icc = PngCodecTests.SyntheticIcc("RGB ") };
        byte[] withIcc = PngOptimizer.Optimize(gray, rgbIcc, new PngOptimizeOptions(KeepIcc: true));
        Assert.NotEqual((byte)PngColorType.Gray, PngCodecTests.ReadIhdr(withIcc).ColorType);
        using (var decoded = PngTestFiles.Decode(new MemoryStream(withIcc)))
        {
            Assert.Equal(rgbIcc.Icc, decoded.Metadata.Icc);
        }

        byte[] dropped = PngOptimizer.Optimize(gray, rgbIcc, new PngOptimizeOptions(KeepIcc: false));
        Assert.Equal((byte)PngColorType.Gray, PngCodecTests.ReadIhdr(dropped).ColorType);
        using var noIcc = PngTestFiles.Decode(new MemoryStream(dropped));
        Assert.Null(noIcc.Metadata.Icc);
    }

    [Fact]
    public void Optimize_16bitExact_DropsTo8()
    {
        using var exact = Make("exact16");
        byte[] png = PngOptimizer.Optimize(exact, null, new PngOptimizeOptions(CleanInvisiblePixels: false));
        Assert.Equal(8, PngCodecTests.ReadIhdr(png).BitDepth);
        using var decoded = PngTestFiles.Decode(new MemoryStream(png));
        Assert.Equal(SampleDepth.Eight, decoded.Pixels.Depth);
        SameValues(exact, decoded.Pixels);

        // Real 16-bit precision is kept.
        using var deep = TestImages.Deep16();
        Assert.Equal(16, PngCodecTests.ReadIhdr(PngOptimizer.Optimize(deep, null, new PngOptimizeOptions())).BitDepth);
    }

    [Fact]
    public void Optimize_NeverLargerThanFastEncode()
    {
        foreach (string name in ImageNames)
        {
            using var px = Make(name);
            var fast = new MemoryStream();
            PngEncoder.EncodeFast(fast, px, null);
            foreach (int effort in new[] { 1, 2, 3 })
            {
                byte[] optimized = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(CleanInvisiblePixels: false, Effort: effort));
                Assert.True(optimized.Length <= fast.Length, $"{name} effort {effort}: {optimized.Length} > {fast.Length}");
            }
        }
    }

    [Fact]
    public void Optimize_HigherEffortIsNotLarger()
    {
        foreach (string name in new[] { "photo", "graphic", "alpha" })
        {
            using var px = Make(name);
            int e1 = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(Effort: 1)).Length;
            int e2 = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(Effort: 2)).Length;
            int e3 = PngOptimizer.Optimize(px, null, new PngOptimizeOptions(Effort: 3)).Length;
            Assert.True(e2 <= e1 && e3 <= e2, $"{name}: {e1} {e2} {e3}");
        }
    }

    [Fact]
    public void Optimize_KeepsOnlyRequestedMetadata()
    {
        using var px = TestImages.PhotoLike(32, 24);
        var meta = new ImageMetadata
        {
            Icc = PngCodecTests.SyntheticIcc("RGB "),
            Exif = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            Xmp = "<x:xmpmeta/>"u8.ToArray(),
        };
        using (var all = PngTestFiles.Decode(new MemoryStream(PngOptimizer.Optimize(px, meta, new PngOptimizeOptions(KeepIcc: true, KeepExif: true)))))
        {
            Assert.Equal(meta.Icc, all.Metadata.Icc);
            Assert.Equal(meta.Exif, all.Metadata.Exif);
            Assert.Equal(meta.Xmp, all.Metadata.Xmp);
        }

        using var none = PngTestFiles.Decode(new MemoryStream(PngOptimizer.Optimize(px, meta, new PngOptimizeOptions(KeepIcc: false, KeepExif: false))));
        Assert.Null(none.Metadata.Icc);
        Assert.Null(none.Metadata.Exif);
        Assert.Null(none.Metadata.Xmp);
    }

    [Fact]
    public void Optimize_RejectsInvalidEffort()
    {
        using var px = TestImages.Gray(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => PngOptimizer.Optimize(px, null, new PngOptimizeOptions(Effort: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngOptimizer.Optimize(px, null, new PngOptimizeOptions(Effort: 4)));
    }

    private static (byte ColorType, byte BitDepth) Type(byte[] png)
    {
        var i = PngCodecTests.ReadIhdr(png);
        return (i.ColorType, i.BitDepth);
    }

    // Every channel equal at 16-bit precision, invisible RGB included (depths may differ).
    private static void SameValues(PixelBuffer expected, PixelBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(PixelAssert.At(expected, x, y), PixelAssert.At(actual, x, y));
            }
        }
    }

    private static PixelBuffer Make(string name) => name switch
    {
        "gradient" => TestImages.Gradient(96, 64),
        "photo" => TestImages.PhotoLike(96, 64),
        "graphic" => TestImages.Graphic(),
        "alpha" => TestImages.WithAlpha(96, 64),
        "palette16" => TestImages.Palette(64, 48, 16),
        "palette256" => TestImages.Palette(64, 48, 256),
        "gray" => TestImages.Gray(),
        "deep16" => TestImages.Deep16(),
        "exact16" => Exact16(),
        "binaryAlpha" => BinaryAlpha(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // 64×64 dithered-looking image using exactly `count` distinct opaque colours (each appears at least once).
    private static PixelBuffer DistinctColors(int count)
    {
        var rng = new Random(count);
        var px = PixelBuffer.Create(64, 64, SampleDepth.Eight);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int n = (y * 64) + x;
                int i = n < count ? n : rng.Next(count);
                px.Row32(y)[x] = new Rgba32((byte)(i * 37), (byte)(i * 91), (byte)(i * 151));
            }
        }

        return px;
    }

    private static PixelBuffer Exact16()
    {
        using var eight = TestImages.WithAlpha(48, 32);
        return eight.ToDepth(SampleDepth.Sixteen);
    }

    // Photo with a binary-alpha hole whose invisible pixels carry assorted colours.
    private static PixelBuffer BinaryAlpha()
    {
        var px = TestImages.PhotoLike(64, 48, seed: 5);
        for (int y = 10; y < 30; y++)
        {
            for (int x = 20; x < 50; x++)
            {
                px.Row32(y)[x] = new Rgba32((byte)x, (byte)y, (byte)(x * y), 0);
            }
        }

        return px;
    }
}
