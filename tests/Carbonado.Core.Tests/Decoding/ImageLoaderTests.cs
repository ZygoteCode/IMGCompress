using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;
using ImageMetadata = Carbonado.Core.Imaging.ImageMetadata;

namespace Carbonado.Core.Tests.Decoding;

public class ImageLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, ImageFormat, bool> Formats => new()
    {
        { "png", ImageFormat.Png, true },
        { "apng", ImageFormat.Apng, true },
        { "pam", ImageFormat.Pnm, true },
        { "jpeg", ImageFormat.Jpeg, false },
        { "bmp", ImageFormat.Bmp, true },
        { "gif", ImageFormat.Gif, true },
        { "tiff", ImageFormat.Tiff, true },
        { "jxr", ImageFormat.Jxr, true },
        { "ico", ImageFormat.Ico, true },
        { "webp", ImageFormat.WebP, true },
        { "avif", ImageFormat.Avif, true },
        { "jxl", ImageFormat.Jxl, true },
        { "qoi", ImageFormat.Qoi, true },
        { "tga", ImageFormat.Tga, true },
        { "jp2", ImageFormat.Jp2, false },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Loader_LoadsEveryFormat(string format, ImageFormat expected, bool lossless)
    {
        if (FormatFixtures.ToolFor(format) is { } tool)
        {
            TestTools.Require(tool);
        }

        using var dir = new TempDir("loader");
        using var source = FormatFixtures.Blocks();
        string path = await FormatFixtures.WriteAsync(format, source, dir);
        using var image = await FormatFixtures.Loader(dir).LoadAsync(path, new LoadOptions(), Ct);
        Assert.Equal(expected, image.SourceFormat);
        Assert.Equal((source.Width, source.Height), (image.Pixels.Width, image.Pixels.Height));
        if (lossless)
        {
            PixelAssert.VisiblyIdentical(source, image.Pixels);
        }
        else
        {
            Assert.InRange(PixelAssert.MeanDifference8(source, image.Pixels), 0, 6);
        }

        Assert.Empty(Directory.EnumerateFiles(dir.Sub("work")));
    }

    [Fact]
    public async Task Loader_KeepsAlphaThroughAlphaCapableFormats()
    {
        using var dir = new TempDir("loader");
        using var source = TestImages.WithAlpha(40, 28);
        foreach (string format in new[] { "png", "pam", "webp", "avif", "jxl", "qoi", "tga" })
        {
            if (FormatFixtures.ToolFor(format) is { } tool && TestTools.Path(tool) is null)
            {
                continue;
            }

            string path = await FormatFixtures.WriteAsync(format, source, dir);
            using var image = await FormatFixtures.Loader(dir).LoadAsync(path, new LoadOptions(), Ct);
            PixelAssert.VisiblyIdentical(source, image.Pixels);
        }
    }

    [Theory]
    [InlineData((ushort)6)]
    [InlineData((ushort)8)]
    public async Task Loader_AppliesExifOrientation(ushort orientation)
    {
        using var dir = new TempDir("loader");
        string path = WriteOrientedJpeg(dir, orientation, out int w, out int h);
        var loader = FormatFixtures.Loader(dir);

        using (var upright = await loader.LoadAsync(path, new LoadOptions(), Ct))
        {
            Assert.Equal((h, w), (upright.Pixels.Width, upright.Pixels.Height));
            Assert.Equal(1, upright.Metadata.Orientation);
            Assert.Equal(1, ExifReader.ReadOrientation(upright.Metadata.Exif));
            Assert.True(ExifReader.HasGps(upright.Metadata.Exif), "the loader keeps metadata; policies strip it");

            // The red marker in the stored top-left corner moves to the displayed top-right (6) or bottom-left (8).
            var (x, y) = orientation == 6 ? (h - 4, 3) : (3, w - 4);
            Rgba32 marker = upright.Pixels.Row32(y)[x];
            Assert.True(marker.R > 200 && marker.G < 60 && marker.B < 60, $"marker at ({x},{y}) is {marker}");
        }

        using var stored = await loader.LoadAsync(path, new LoadOptions(ApplyOrientation: false), Ct);
        Assert.Equal((w, h), (stored.Pixels.Width, stored.Pixels.Height));
        Assert.Equal(orientation, stored.Metadata.Orientation);
        Assert.Equal(orientation, ExifReader.ReadOrientation(stored.Metadata.Exif));
    }

    [Fact]
    public async Task Loader_JxlAndAvif_ApplyContainerOrientationOnce()
    {
        string cjxl = TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Avifenc);
        using var dir = new TempDir("loader");
        string jpeg = WriteOrientedJpeg(dir, 6, out int w, out int h);
        var loader = FormatFixtures.Loader(dir);

        await FormatFixtures.Run(ToolId.Cjxl, [jpeg, dir.File("o.jxl"), "--lossless_jpeg=1", "--quiet"], Ct);
        await FormatFixtures.Run(ToolId.Avifenc, ["-s", "9", jpeg, dir.File("o.avif")], Ct);
        Assert.False(string.IsNullOrEmpty(cjxl));
        foreach (string file in new[] { "o.jxl", "o.avif" })
        {
            using var image = await loader.LoadAsync(dir.File(file), new LoadOptions(), Ct);
            Assert.Equal((h, w), (image.Pixels.Width, image.Pixels.Height));
            Assert.Equal(1, image.Metadata.Orientation);
            Assert.NotNull(image.Metadata.Exif);
            Assert.Equal(1, ExifReader.ReadOrientation(image.Metadata.Exif));
            Rgba32 marker = image.Pixels.Row32(3)[h - 4];
            Assert.True(marker.R > 180 && marker.G < 80 && marker.B < 80, $"{file}: marker {marker}");
        }
    }

    [Fact]
    public async Task Loader_CmykJpeg_ConvertsToRgb()
    {
        using var dir = new TempDir("loader");
        using var source = ColorBlocks();
        var cmyk = new FormatConvertedBitmap(TestImages.ToBitmapSource(source), PixelFormats.Cmyk32, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(cmyk));
        string path = dir.File("cmyk.jpg");
        using (var fs = File.Create(path))
        {
            encoder.Save(fs);
        }

        string swop = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color", "RSWOP.icm");
        var variants = new List<string> { path };
        if (File.Exists(swop))
        {
            string tagged = dir.File("cmyk-swop.jpg");
            File.WriteAllBytes(tagged, JpegSegments.Rewrite(File.ReadAllBytes(path), null, null, File.ReadAllBytes(swop), keepOtherMetadata: true));
            variants.Add(tagged);
        }

        // Windows' default CMYK conversion uses its default profile (RSWOP), the only CMYK profile shipped with the OS,
        // so both variants must decode to plausible, equal colours.
        var blues = new List<Rgba32>();
        foreach (string file in variants)
        {
            using var image = await FormatFixtures.Loader(dir).LoadAsync(file, new LoadOptions(), Ct);
            Assert.Equal(SampleDepth.Eight, image.Pixels.Depth);
            Assert.Null(image.Metadata.Icc);
            AssertDominant(image.Pixels.Row32(4)[4], 'R', file);
            AssertDominant(image.Pixels.Row32(4)[20], 'G', file);
            AssertDominant(image.Pixels.Row32(4)[36], 'B', file);
            Rgba32 white = image.Pixels.Row32(20)[4], black = image.Pixels.Row32(20)[20];
            Assert.True(white.R > 200 && white.G > 200 && white.B > 200, $"{file}: white {white}");
            Assert.True(black.R < 70 && black.G < 70 && black.B < 70, $"{file}: black {black}");
            blues.Add(image.Pixels.Row32(4)[36]);
        }

        Assert.All(blues, b => Assert.True(Math.Abs(b.B - blues[0].B) <= 3, $"{b} vs {blues[0]}"));
    }

    [Fact]
    public async Task Loader_JpegWithUnreadableXmp_StillDecodes()
    {
        // WIC refuses to open JPEGs whose XMP it cannot parse; the loader falls back to the pixels alone.
        using var dir = new TempDir("loader");
        using var px = TestImages.PhotoLike(40, 30);
        TestImages.SaveJpegWithWic(px, dir.File("plain.jpg"));
        byte[] badXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        File.WriteAllBytes(dir.File("bad-xmp.jpg"), JpegSegments.Rewrite(File.ReadAllBytes(dir.File("plain.jpg")), ExifTestData.Build(false, 6, withGps: false), badXmp, null, keepOtherMetadata: true));
        using var image = await FormatFixtures.Loader(dir).LoadAsync(dir.File("bad-xmp.jpg"), new LoadOptions(), Ct);
        Assert.Equal((30, 40), (image.Pixels.Width, image.Pixels.Height));
        Assert.Equal(badXmp, image.Metadata.Xmp);
    }

    [Fact]
    public async Task Loader_16bitPng_Keeps16()
    {
        using var dir = new TempDir("loader");
        using var deep = TestImages.Deep16();
        using (var fs = File.Create(dir.File("deep.png")))
        {
            PngEncoder.Encode(fs, deep, null, new PngEncodeOptions());
        }

        using var image = await FormatFixtures.Loader(dir).LoadAsync(dir.File("deep.png"), new LoadOptions(), Ct);
        Assert.Equal(SampleDepth.Sixteen, image.Pixels.Depth);
        Assert.Equal(16, image.SignificantBits);
        PixelAssert.Identical(deep, image.Pixels);
    }

    [Fact]
    public async Task Loader_ReadsIccFromJpegAndPng()
    {
        using var dir = new TempDir("loader");
        using var px = TestImages.PhotoLike(40, 30);

        // 150 KB: three APP2 chunks in JPEG.
        byte[] icc = PngCodecTests.SyntheticIcc("RGB ");
        Array.Resize(ref icc, 150_000);
        new Random(5).NextBytes(icc.AsSpan(200));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)icc.Length);

        TestImages.SaveJpegWithWic(px, dir.File("plain.jpg"));
        File.WriteAllBytes(dir.File("icc.jpg"), JpegSegments.Rewrite(File.ReadAllBytes(dir.File("plain.jpg")), null, null, icc, keepOtherMetadata: true));
        using (var fs = File.Create(dir.File("icc.png")))
        {
            PngEncoder.Encode(fs, px, new ImageMetadata { Icc = icc }, new PngEncodeOptions());
        }

        var loader = FormatFixtures.Loader(dir);
        using (var jpeg = await loader.LoadAsync(dir.File("icc.jpg"), new LoadOptions(), Ct))
        {
            Assert.Equal(icc, jpeg.Metadata.Icc);
        }

        using (var png = await loader.LoadAsync(dir.File("icc.png"), new LoadOptions(), Ct))
        {
            Assert.Equal(icc, png.Metadata.Icc);
        }

        using var none = await loader.LoadAsync(dir.File("plain.jpg"), new LoadOptions(), Ct);
        Assert.Null(none.Metadata.Icc);
    }

    [Fact]
    public async Task Loader_CorruptFile_ThrowsInvalidData()
    {
        using var dir = new TempDir("loader");
        var loader = FormatFixtures.Loader(dir);
        using var px = TestImages.PhotoLike(64, 48);

        File.WriteAllBytes(dir.File("empty.png"), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("empty.png"), new LoadOptions(), Ct));

        File.WriteAllBytes(dir.File("noise.png"), Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("noise.png"), new LoadOptions(), Ct));

        TestImages.SaveJpegWithWic(px, dir.File("full.jpg"));
        byte[] jpeg = File.ReadAllBytes(dir.File("full.jpg"));
        File.WriteAllBytes(dir.File("half.jpg"), jpeg[..(jpeg.Length / 2)]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("half.jpg"), new LoadOptions(), Ct));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));

        using (var blocks = FormatFixtures.Blocks())
        {
            string gif = await FormatFixtures.WriteAsync("gif", blocks, dir);
            byte[] gifBytes = File.ReadAllBytes(gif);
            File.WriteAllBytes(dir.File("half.gif"), gifBytes[..(gifBytes.Length * 2 / 3)]);
            await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("half.gif"), new LoadOptions(), Ct));
        }

        byte[] png = PngTestFiles.Still(px);
        File.WriteAllBytes(dir.File("half.png"), png[..(png.Length / 2)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("half.png"), new LoadOptions(), Ct));

        if (TestTools.Path(ToolId.Cwebp) is not null)
        {
            string webp = await FormatFixtures.WriteAsync("webp", px, dir);
            byte[] bytes = File.ReadAllBytes(webp);
            File.WriteAllBytes(dir.File("half.webp"), bytes[..(bytes.Length / 2)]);
            await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(dir.File("half.webp"), new LoadOptions(), Ct));
        }

        // Content decides: a PNG named .jpg decodes as PNG.
        File.WriteAllBytes(dir.File("really-png.jpg"), png);
        using var misnamed = await loader.LoadAsync(dir.File("really-png.jpg"), new LoadOptions(), Ct);
        Assert.Equal(ImageFormat.Png, misnamed.SourceFormat);

        await Assert.ThrowsAsync<FileNotFoundException>(() => loader.LoadAsync(dir.File("missing.png"), new LoadOptions(), Ct));
        Assert.Empty(Directory.EnumerateFiles(dir.Sub("work")));
    }

    [Fact]
    public async Task Loader_AnimatedSources_DecodeFirstFrameAndCount()
    {
        TestTools.Require(ToolId.Gif2webp);
        using var dir = new TempDir("loader");
        using var a = FormatFixtures.Blocks();
        using var b = TestImages.Palette(48, 32, 7);
        FormatFixtures.AnimatedGif(dir.File("anim.gif"), a, b);

        await FormatFixtures.Run(ToolId.Gif2webp, ["-quiet", dir.File("anim.gif"), "-o", dir.File("anim.webp")], Ct);
        File.WriteAllBytes(dir.File("anim.png"), PngTestFiles.Apng(a, b));
        var loader = FormatFixtures.Loader(dir);
        foreach (string file in new[] { "anim.gif", "anim.webp", "anim.png" })
        {
            using var image = await loader.LoadAsync(dir.File(file), new LoadOptions(), Ct);
            Assert.True(image.FrameCount >= 2, $"{file}: {image.FrameCount} frames");
            PixelAssert.VisiblyIdentical(a, image.Pixels);
            await Assert.ThrowsAsync<NotSupportedException>(() => loader.LoadAsync(dir.File(file), new LoadOptions(FirstFrameOnly: false), Ct));
        }
    }

    [Fact]
    public async Task Loader_HandlesUnicodeAndLongPaths()
    {
        TestTools.Require(ToolId.Cwebp);
        using var dir = new TempDir("loader");
        using var px = FormatFixtures.Blocks();
        string deep = dir.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, "àèìòù 日本 cartella");
        }

        Directory.CreateDirectory(deep);
        string webp = await FormatFixtures.WriteAsync("webp", px, dir);
        string png = await FormatFixtures.WriteAsync("png", px, dir);
        var loader = FormatFixtures.Loader(dir);
        foreach (string file in new[] { webp, png })
        {
            string target = Path.Combine(deep, "àèìòù 日本 x y" + Path.GetExtension(file));
            File.Copy(file, target, overwrite: true);
            Assert.True(target.Length > 260);
            using var image = await loader.LoadAsync(target, new LoadOptions(), Ct);
            PixelAssert.VisiblyIdentical(px, image.Pixels);
        }
    }

    [Fact]
    public async Task DecodeOutput_RoundTripsEachOutputFormat()
    {
        using var dir = new TempDir("loader");
        using var px = FormatFixtures.Blocks();
        var loader = FormatFixtures.Loader(dir);
        foreach (var (format, fixture, lossless) in new (OutputFormat, string, bool)[]
        {
            (OutputFormat.Png, "png", true), (OutputFormat.Gif, "gif", true), (OutputFormat.Jpeg, "jpeg", false),
            (OutputFormat.WebP, "webp", true), (OutputFormat.Avif, "avif", true), (OutputFormat.Jxl, "jxl", true),
        })
        {
            if (FormatFixtures.ToolFor(fixture) is { } tool && TestTools.Path(tool) is null)
            {
                continue;
            }

            string path = await FormatFixtures.WriteAsync(fixture, px, dir);
            using var decoded = await loader.DecodeOutputAsync(path, format, Ct);
            Assert.Equal((px.Width, px.Height), (decoded.Width, decoded.Height));
            if (lossless)
            {
                PixelAssert.VisiblyIdentical(px, decoded);
            }
            else
            {
                Assert.InRange(PixelAssert.MeanDifference8(px, decoded), 0, 6);
            }
        }
    }

    [Fact]
    public void FfmpegStreamParsing_ReadsOutputSizeAndInputDepth()
    {
        const string Log = """
            Input #0, png_pipe, from 'x.png':
              Stream #0:0: Video: png, rgba64be(pc, gbr/bt709/iec61966-2-1), 64x40 [SAR 3779:3779 DAR 8:5], 25 fps
            Output #0, rawvideo, to 'x.raw':
              Stream #0:0: Video: rawvideo (RBA[64] / 0x40414252), rgba64le(pc, gbr/bt709/iec61966-2-1, progressive), 64x40 [SAR 1:1 DAR 8:5], q=2-31
            """;
        Assert.Equal((64, 40, true, true), ToolDecoders.ParseFfmpegStreams(Log));
        Assert.Equal((4032, 3024, false, true), ToolDecoders.ParseFfmpegStreams(Log.Replace("rgba64be", "yuvj420p").Replace("64x40", "4032x3024")));
        Assert.True(ToolDecoders.ParseFfmpegStreams(Log.Replace("rgba64be", "gbrpf32le")).HighDepth);
        Assert.True(ToolDecoders.ParseFfmpegStreams(Log.Replace("rgba64be", "yuv420p10le")).HighDepth);
        Assert.False(ToolDecoders.ParseFfmpegStreams(Log.Replace("rgba64be", "yuv410p")).HighDepth);
        Assert.Equal((0, 0, false, false), ToolDecoders.ParseFfmpegStreams("garbage"));
    }

    // 48×32 JPEG with a red 8×8 marker in the stored top-left corner and EXIF orientation/GPS.
    private static string WriteOrientedJpeg(TempDir dir, ushort orientation, out int width, out int height)
    {
        using var px = PixelBuffer.Create(48, 32, SampleDepth.Eight);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                px.Row32(y)[x] = x < 8 && y < 8 ? new Rgba32(255, 0, 0) : new Rgba32(40, 90, 200);
            }
        }

        string plain = dir.File($"plain-{orientation}.jpg");
        TestImages.SaveJpegWithWic(px, plain, 95);
        string path = dir.File($"oriented-{orientation}.jpg");
        File.WriteAllBytes(path, JpegSegments.Rewrite(File.ReadAllBytes(plain), ExifTestData.Build(false, orientation, withGps: true), null, null, keepOtherMetadata: true));
        width = px.Width;
        height = px.Height;
        return path;
    }

    // 16×16 blocks: red, green, blue on row 0; white, black on row 1.
    private static PixelBuffer ColorBlocks()
    {
        var colors = new[] { new Rgba32(230, 20, 20), new Rgba32(20, 200, 30), new Rgba32(20, 30, 220), new Rgba32(255, 255, 255), new Rgba32(0, 0, 0), new Rgba32(128, 128, 128) };
        var px = PixelBuffer.Create(48, 32, SampleDepth.Eight);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                px.Row32(y)[x] = colors[(x / 16) + ((y / 16) * 3)];
            }
        }

        return px;
    }

    // Profile-based conversions (SWOP) render saturated primaries duller than the naive formula: require a clear lead only.
    private static void AssertDominant(Rgba32 p, char channel, string file)
    {
        int r = p.R, g = p.G, b = p.B;
        bool ok = channel switch
        {
            'R' => r > g + 40 && r > b + 40,
            'G' => g > r + 40 && g > b + 40,
            _ => b > r + 40 && b > g + 40,
        };
        Assert.True(ok, $"{Path.GetFileName(file)}: {channel} not dominant in {p}");
    }
}
