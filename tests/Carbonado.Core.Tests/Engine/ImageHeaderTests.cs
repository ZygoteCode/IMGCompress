using Carbonado.Core.Codecs;
using Carbonado.Core.Engine;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Engine;

public class ImageHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("png")]
    [InlineData("apng")]
    [InlineData("pam")]
    [InlineData("jpeg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    [InlineData("ico")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("jxl")]
    [InlineData("qoi")]
    [InlineData("tga")]
    public async Task Header_ReadsTheSizeOfEveryCommonFormat(string format)
    {
        if (FormatFixtures.ToolFor(format) is { } tool)
        {
            TestTools.Require(tool);
        }

        using var dir = new TempDir("header");
        using var px = FormatFixtures.Blocks(48, 32);
        string path = await FormatFixtures.WriteAsync(format, px, dir);

        var info = ImageHeader.TryRead(path);

        Assert.NotNull(info);
        Assert.Equal((48, 32), (info.Value.Width, info.Value.Height));
    }

    [Fact]
    public void Header_PngReportsAlphaAndDepth()
    {
        using var dir = new TempDir("header");
        using var opaque = TestImages.Gradient(64, 40);
        using var alpha = TestImages.WithAlpha(64, 40);
        using var deep = TestImages.Deep16(64, 40);

        var o = ImageHeader.TryRead(EngineTestSupport.WritePng(dir.File("o.png"), opaque))!.Value;
        var a = ImageHeader.TryRead(EngineTestSupport.WritePng(dir.File("a.png"), alpha))!.Value;
        var d = ImageHeader.TryRead(EngineTestSupport.WritePng(dir.File("d.png"), deep))!.Value;

        Assert.False(o.MayHaveAlpha);
        Assert.False(o.Deep);
        Assert.True(a.MayHaveAlpha);
        Assert.True(d.Deep);
    }

    [Fact]
    public async Task Header_WebPLossyLosslessAndAlpha()
    {
        TestTools.Require(ToolId.Cwebp);
        using var dir = new TempDir("header");
        using var photo = TestImages.PhotoLike(150, 90);
        using var alpha = TestImages.WithAlpha(150, 90);
        string photoPng = EngineTestSupport.WritePng(dir.File("p.png"), photo);
        string alphaPng = EngineTestSupport.WritePng(dir.File("a.png"), alpha);
        await FormatFixtures.Run(ToolId.Cwebp, ["-quiet", "-q", "70", photoPng, "-o", dir.File("lossy.webp")], Ct);
        await FormatFixtures.Run(ToolId.Cwebp, ["-quiet", "-lossless", photoPng, "-o", dir.File("lossless.webp")], Ct);
        await FormatFixtures.Run(ToolId.Cwebp, ["-quiet", "-q", "70", alphaPng, "-o", dir.File("alpha.webp")], Ct);

        var lossy = ImageHeader.TryRead(dir.File("lossy.webp"))!.Value;
        var lossless = ImageHeader.TryRead(dir.File("lossless.webp"))!.Value;
        var withAlpha = ImageHeader.TryRead(dir.File("alpha.webp"))!.Value;

        Assert.Equal((150, 90, false), (lossy.Width, lossy.Height, lossy.MayHaveAlpha));
        Assert.Equal((150, 90, false), (lossless.Width, lossless.Height, lossless.MayHaveAlpha));
        Assert.Equal((150, 90, true), (withAlpha.Width, withAlpha.Height, withAlpha.MayHaveAlpha));
    }

    [Theory]
    [InlineData(1000, 600)]
    [InlineData(640, 480)]
    [InlineData(300, 300)]
    [InlineData(513, 7)]
    [InlineData(2048, 1152)]
    public async Task Header_JxlSizeHeader(int width, int height)
    {
        TestTools.Require(ToolId.Cjxl);
        using var dir = new TempDir("header");
        using var px = TestImages.Gradient(width, height);
        string png = EngineTestSupport.WritePng(dir.File("g.png"), px);
        await FormatFixtures.Run(ToolId.Cjxl, [png, dir.File("g.jxl"), "-d", "0", "-e", "1", "--quiet"], Ct);

        var info = ImageHeader.TryRead(dir.File("g.jxl"));

        Assert.NotNull(info);
        Assert.Equal((width, height), (info.Value.Width, info.Value.Height));
    }

    [Fact]
    public async Task Header_JxlContainerFromAJpegTranscode()
    {
        TestTools.Require(ToolId.Cjxl);
        using var dir = new TempDir("header");
        using (var px = TestImages.PhotoLike(200, 136))
        {
            TestImages.SaveJpegWithWic(px, dir.File("p.jpg"), 85);
        }

        await FormatFixtures.Run(ToolId.Cjxl, [dir.File("p.jpg"), dir.File("p.jxl"), "--lossless_jpeg=1", "--quiet"], Ct);
        Assert.Equal(0, File.ReadAllBytes(dir.File("p.jxl"))[0]); // a box container, not a bare codestream

        var info = ImageHeader.TryRead(dir.File("p.jxl"));

        Assert.Equal((200, 136), (info!.Value.Width, info.Value.Height));
    }

    [Fact]
    public void Header_JpegFrameAfterLargeSegments()
    {
        using var dir = new TempDir("header");
        using (var px = TestImages.PhotoLike(120, 80))
        {
            TestImages.SaveJpegWithWic(px, dir.File("plain.jpg"), 85);
        }

        // ~60 KB of EXIF and a 200 KB ICC profile (split over several APP2 segments) before the frame header.
        byte[] exif = [.. "II*\0"u8, 8, 0, 0, 0, 0, 0, .. new byte[60_000]];
        byte[] icc = TestIcc.DisplayP3Gamma22();
        byte[] bigIcc = [.. icc, .. new byte[200_000]];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bigIcc, (uint)bigIcc.Length);
        File.WriteAllBytes(dir.File("big.jpg"), JpegSegments.Rewrite(File.ReadAllBytes(dir.File("plain.jpg")), exif, null, bigIcc, keepOtherMetadata: false));

        var info = ImageHeader.TryRead(dir.File("big.jpg"));

        Assert.True(new FileInfo(dir.File("big.jpg")).Length > 250_000);
        Assert.Equal((120, 80, false), (info!.Value.Width, info.Value.Height, info.Value.MayHaveAlpha));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void Header_DamagedOrUnknownIsNull(byte[] data)
    {
        using var dir = new TempDir("header");
        File.WriteAllBytes(dir.File("bad.bin"), data);

        Assert.Null(ImageHeader.TryRead(dir.File("bad.bin")));
        Assert.Null(ImageHeader.TryRead(dir.File("missing.png")));
    }
}
