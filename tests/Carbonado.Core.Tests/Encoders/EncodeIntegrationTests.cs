using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;
using static Carbonado.Core.Tests.Encoders.EncoderTestSupport;

namespace Carbonado.Core.Tests.Encoders;

public class EncodeIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, EncodeKind, string> Encodes()
    {
        var data = new TheoryData<string, EncodeKind, string>();
        foreach (string image in new[] { "photo", "graphic", "alpha" })
        {
            data.Add("jxl.cjxl", EncodeKind.Lossy, image);
            data.Add("jxl.cjxl", EncodeKind.Lossless, image);
            data.Add("avif.avifenc", EncodeKind.Lossy, image);
            data.Add("avif.avifenc", EncodeKind.Lossless, image);
            data.Add("webp.cwebp", EncodeKind.Lossy, image);
            data.Add("webp.cwebp", EncodeKind.Lossless, image);
            data.Add("png.oxipng", EncodeKind.Lossless, image);
            data.Add("png.ect", EncodeKind.Lossless, image);
            data.Add("png.pngquant", EncodeKind.Lossy, image);
            data.Add("png.builtin", EncodeKind.Lossless, image);
            data.Add("jxl.ffmpeg", EncodeKind.Lossy, image);
            data.Add("jxl.ffmpeg", EncodeKind.Lossless, image);
            data.Add("webp.ffmpeg", EncodeKind.Lossy, image);
            data.Add("webp.ffmpeg", EncodeKind.Lossless, image);
            if (image != "alpha")
            {
                data.Add("jpeg.sjpeg", EncodeKind.Lossy, image);
                data.Add("avif.ffmpeg", EncodeKind.Lossy, image);
            }
        }

        data.Add("avif.avifenc", EncodeKind.Lossy, "deep");
        data.Add("jxl.cjxl", EncodeKind.Lossless, "deep");
        data.Add("png.oxipng", EncodeKind.Lossless, "deep");
        data.Add("avif.avifenc", EncodeKind.Lossy, "gray");
        data.Add("jxl.cjxl", EncodeKind.Lossless, "gray");
        return data;
    }

    [Theory]
    [MemberData(nameof(Encodes))]
    public async Task Encode_RealToolsProduceDecodableFiles(string id, EncodeKind kind, string image)
    {
        var adapter = RequireAdapter(id);
        TestTools.Require(ToolId.Djxl);
        TestTools.Require(ToolId.Avifdec);
        TestTools.Require(ToolId.Dwebp);
        using var dir = new TempDir("encode");
        using var px = Image(image);
        using var source = Source(dir, px);
        Assert.True(adapter.Supports(kind, source.Analysis, ImageFormat.Png));
        double? parameter = kind == EncodeKind.Lossy ? adapter.InitialGuess(85, source.Analysis) : null;
        string output = dir.File($"out{FormatInfo.Extension(adapter.Format)}");
        var outcome = await adapter.EncodeAsync(new EncodeRequest(source, kind, parameter, EffortLevel.Fast, ChromaMode.Auto, 2, output), Ct);

        Assert.True(outcome.Success, $"{id}: {outcome.Error}\n{outcome.CommandLine}");
        Assert.Null(outcome.Error);
        Assert.True(outcome.Bytes > 0);
        Assert.Equal(new FileInfo(output).Length, outcome.Bytes);
        Assert.Equal(output, outcome.OutputPath);
        Assert.False(string.IsNullOrWhiteSpace(outcome.CommandLine));

        using var decoded = await FormatFixtures.Loader(dir).DecodeOutputAsync(output, adapter.Format, Ct);
        Assert.Equal((px.Width, px.Height), (decoded.Width, decoded.Height));
        if (kind == EncodeKind.Lossless)
        {
            PixelAssert.VisiblyIdentical(px, decoded);
        }
        else
        {
            Assert.InRange(PixelAssert.MeanDifference8(px, decoded), 0, 14);
        }

        // Temporary outputs never linger in the work directory.
        Assert.DoesNotContain(Directory.EnumerateFiles(source.WorkDir), f => Path.GetFileName(f).StartsWith("out-", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("tmp-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("jxl.cjxl")]
    [InlineData("webp.cwebp")]
    [InlineData("png.oxipng")]
    [InlineData("png.ect")]
    [InlineData("png.builtin")]
    [InlineData("avif.avifenc")]
    public async Task Lossless_IsPixelExact(string id)
    {
        var adapter = RequireAdapter(id);
        foreach (bool preserve in new[] { false, true })
        {
            using var dir = new TempDir("lossless");
            using var px = TestImages.WithAlpha(64, 48);
            using var source = Source(dir, px, preserveInvisible: preserve);
            string output = dir.File("out" + FormatInfo.Extension(adapter.Format));
            var outcome = await adapter.EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Balanced, ChromaMode.Auto, 2, output), Ct);
            Assert.True(outcome.Success, outcome.Error);
            using var decoded = await FormatFixtures.Loader(dir).DecodeOutputAsync(output, adapter.Format, Ct);
            if (preserve && id != "avif.avifenc")
            {
                PixelAssert.Identical(px, decoded);
            }
            else
            {
                PixelAssert.VisiblyIdentical(px, decoded);
            }
        }
    }

    [Fact]
    public async Task JpegTranscode_ReconstructsBitExact()
    {
        TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Djxl);
        string photo = TestImages.SystemPhotos(1) is [var first, ..] ? first : Skip("No Windows wallpaper JPEG available.");
        using var dir = new TempDir("transcode");

        // An original with EXIF (orientation 6, GPS) and XMP, as a camera would write it.
        byte[] exif = ExifTestData.Build(false, 6, withGps: true);
        byte[] xmp = ExifTestData.Xmp;
        string original = dir.File("camera 日本.jpg");
        File.WriteAllBytes(original, JpegSegments.Rewrite(File.ReadAllBytes(photo), exif, xmp, null, keepOtherMetadata: true));

        var loader = FormatFixtures.Loader(dir);
        var verifier = new OutputVerifier(loader, Locator, ProcessRunner.Shared);
        var cjxl = Adapter("jxl.cjxl");
        using var decoded = await loader.LoadAsync(original, new LoadOptions(), Ct);
        Assert.Equal(1, ExifReader.ReadOrientation(decoded.Metadata.Exif));

        // KeepAll: nothing changes, so the reference is the original file itself.
        using (var keepAll = Source(dir, decoded.Pixels, decoded.Metadata.Clone(), originalPath: original, format: ImageFormat.Jpeg))
        {
            string reference = await keepAll.GetJpegReferenceAsync(Ct);
            Assert.Equal(original, reference);
            var outcome = await cjxl.EncodeAsync(new EncodeRequest(keepAll, EncodeKind.JpegTranscode, null, EffortLevel.Balanced, ChromaMode.Auto, 2, dir.File("keep.jxl")), Ct);
            Assert.True(outcome.Success, outcome.Error);
            Assert.True(outcome.Bytes < new FileInfo(original).Length * 0.95, $"{outcome.Bytes} vs {new FileInfo(original).Length}");
            Assert.True(await verifier.VerifyJpegReconstructionAsync(dir.File("keep.jxl"), original, Ct));
        }

        // Keep colour profile only: EXIF/XMP go, the orientation survives in a minimal EXIF.
        using var iccOnly = Source(dir, decoded.Pixels, new ImageMetadata { Icc = decoded.Metadata.Icc }, originalPath: original, format: ImageFormat.Jpeg);
        string filtered = await iccOnly.GetJpegReferenceAsync(Ct);
        Assert.NotEqual(original, filtered);
        Assert.StartsWith(iccOnly.WorkDir, filtered);
        using (var fs = File.OpenRead(filtered))
        {
            var meta = JpegSegments.Read(fs);
            Assert.Null(meta.Xmp);
            Assert.Equal(6, ExifReader.ReadOrientation(meta.Exif));
            Assert.False(ExifReader.HasGps(meta.Exif));
        }

        var stripped = await cjxl.EncodeAsync(new EncodeRequest(iccOnly, EncodeKind.JpegTranscode, null, EffortLevel.Fast, ChromaMode.Auto, 2, dir.File("icc.jxl")), Ct);
        Assert.True(stripped.Success, stripped.Error);
        Assert.True(await verifier.VerifyJpegReconstructionAsync(dir.File("icc.jxl"), filtered, Ct));
        Assert.False(await verifier.VerifyJpegReconstructionAsync(dir.File("icc.jxl"), original, Ct));

        // Pixels decoded from the transcode are upright (cjxl moved the orientation into the codestream).
        using var pixels = await loader.DecodeOutputAsync(dir.File("icc.jxl"), OutputFormat.Jxl, Ct);
        Assert.Equal((decoded.Pixels.Width, decoded.Pixels.Height), (pixels.Width, pixels.Height));
    }

    [Fact]
    public async Task JpegOptimize_KeepsCoefficientsAndOrientation()
    {
        var sjpeg = RequireAdapter("jpeg.sjpeg");
        string photo = TestImages.SystemPhotos(1) is [var first, ..] ? first : Skip("No Windows wallpaper JPEG available.");
        using var dir = new TempDir("optimize");
        string original = dir.File("photo.jpg");
        File.WriteAllBytes(original, JpegSegments.Rewrite(File.ReadAllBytes(photo), ExifTestData.Build(false, 8, withGps: true), null, null, keepOtherMetadata: true));
        var loader = FormatFixtures.Loader(dir);
        using var decoded = await loader.LoadAsync(original, new LoadOptions(), Ct);
        using var source = Source(dir, decoded.Pixels, new ImageMetadata(), originalPath: original, format: ImageFormat.Jpeg);
        var outcome = await sjpeg.EncodeAsync(new EncodeRequest(source, EncodeKind.Optimize, null, EffortLevel.Fast, ChromaMode.Auto, 2, dir.File("opt.jpg")), Ct);
        Assert.True(outcome.Success, outcome.Error);
        Assert.True(outcome.Bytes <= new FileInfo(original).Length);

        using (var fs = File.OpenRead(dir.File("opt.jpg")))
        {
            var meta = JpegSegments.Read(fs);
            Assert.Equal(8, ExifReader.ReadOrientation(meta.Exif));
            Assert.False(ExifReader.HasGps(meta.Exif));
        }

        // Same coefficients ⇒ identical pixels once both are decoded upright.
        using var optimized = await loader.LoadAsync(dir.File("opt.jpg"), new LoadOptions(), Ct);
        PixelAssert.Identical(decoded.Pixels, optimized.Pixels);
    }

    [Fact]
    public async Task Encoders_EmbedPolicyMetadata()
    {
        TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("metadata");
        using var px = TestImages.PhotoLike(48, 32);
        byte[] icc = TestIcc.DisplayP3Gamma22();
        var metadata = new ImageMetadata { Icc = icc, Exif = ExifTestData.Build(false, 1, withGps: false), Xmp = ExifTestData.Xmp };
        var loader = FormatFixtures.Loader(dir);
        foreach (var (id, kind) in new[] { ("webp.cwebp", EncodeKind.Lossy), ("jxl.cjxl", EncodeKind.Lossy), ("avif.avifenc", EncodeKind.Lossy), ("jpeg.sjpeg", EncodeKind.Lossy), ("png.pngquant", EncodeKind.Lossy), ("png.oxipng", EncodeKind.Lossless), ("png.ect", EncodeKind.Lossless), ("png.builtin", EncodeKind.Lossless) })
        {
            var adapter = Adapter(id);
            if (adapter.RequiredTools.Any(t => TestTools.Path(t) is null))
            {
                continue;
            }

            using var source = Source(dir, px, metadata.Clone());
            string output = dir.File($"{id}{FormatInfo.Extension(adapter.Format)}");
            double? q = kind == EncodeKind.Lossy ? adapter.InitialGuess(85, source.Analysis) : null;
            var outcome = await adapter.EncodeAsync(new EncodeRequest(source, kind, q, EffortLevel.Fast, ChromaMode.Auto, 2, output), Ct);
            Assert.True(outcome.Success, $"{id}: {outcome.Error}");
            using var reloaded = await loader.LoadAsync(output, new LoadOptions(), Ct);
            if (adapter.Format == OutputFormat.Jxl)
            {
                // libjxl stores colour spaces it can describe natively as an encoding, not as ICC bytes; djxl writes an equivalent profile.
                Assert.True(IccHeader.IsRgb(reloaded.Metadata.Icc), $"{id}: colour profile lost");
            }
            else
            {
                Assert.True(icc.AsSpan().SequenceEqual(reloaded.Metadata.Icc), $"{id}: ICC lost");
            }

            Assert.True(metadata.Exif.AsSpan().SequenceEqual(reloaded.Metadata.Exif), $"{id}: EXIF lost");
            Assert.True(metadata.Xmp.AsSpan().SequenceEqual(reloaded.Metadata.Xmp), $"{id}: XMP lost");
        }
    }

    [Fact]
    public async Task AnimatedGif_StaysAnimated_OrIsRefused()
    {
        TestTools.Require(ToolId.Gif2webp);
        TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("anim");
        using var a = FormatFixtures.Blocks();
        using var b = TestImages.Palette(48, 32, 7);
        string gif = dir.File("anim.gif");
        FormatFixtures.AnimatedGif(gif, a, b);

        var loader = FormatFixtures.Loader(dir);
        using var source = Source(dir, a, originalPath: gif, format: ImageFormat.Gif, frames: 2);
        foreach (var (id, kind) in new[] { ("webp.gif2webp", EncodeKind.Lossless), ("webp.gif2webp", EncodeKind.Lossy), ("jxl.cjxl", EncodeKind.Lossless) })
        {
            var adapter = Adapter(id);
            string output = dir.File($"{id}-{kind}{FormatInfo.Extension(adapter.Format)}");
            double? q = kind == EncodeKind.Lossy ? 75 : null;
            var outcome = await adapter.EncodeAsync(new EncodeRequest(source, kind, q, EffortLevel.Fast, ChromaMode.Auto, 2, output), Ct);
            Assert.True(outcome.Success, $"{id}: {outcome.Error}");
            using var reloaded = await loader.LoadAsync(output, new LoadOptions(), Ct);
            Assert.True(reloaded.FrameCount >= 2, $"{id} lost the animation ({reloaded.FrameCount} frame)");
        }

        // Encoders that would flatten the animation refuse instead.
        foreach (string id in new[] { "webp.cwebp", "avif.avifenc", "png.oxipng", "png.pngquant" })
        {
            var refused = await Adapter(id).EncodeAsync(new EncodeRequest(source, id == "png.oxipng" ? EncodeKind.Lossless : EncodeKind.Lossy, 70, EffortLevel.Fast, ChromaMode.Auto, 1, dir.File("refused.out")), Ct);
            Assert.False(refused.Success);
            Assert.Contains("flatten", refused.Error);
            Assert.False(File.Exists(dir.File("refused.out")));
        }
    }

    [Fact]
    public async Task Encode_Cancellation_LeavesNoOutput()
    {
        var adapter = RequireAdapter("jxl.cjxl");
        using var dir = new TempDir("cancel");
        using var px = TestImages.PhotoLike(1200, 900);
        using var source = Source(dir, px);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string output = dir.File("never.jxl");
        var run = adapter.EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Insane, ChromaMode.Auto, 1, output), cts.Token);
        await Task.Delay(400, Ct);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(output));
        Assert.DoesNotContain(Directory.EnumerateFiles(source.WorkDir), f => Path.GetFileName(f).StartsWith("out-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Encode_ToolFailure_IsReportedNotThrown()
    {
        var adapter = RequireAdapter("png.ect");
        using var dir = new TempDir("fail");
        using var px = TestImages.Graphic(32, 32);
        using var source = Source(dir, px);
        var outcome = await adapter.EncodeAsync(new EncodeRequest(source, EncodeKind.Lossy, 50, EffortLevel.Fast, ChromaMode.Auto, 1, dir.File("x.png")), Ct);
        Assert.False(outcome.Success);
        Assert.Contains("Lossy", outcome.Error);
        Assert.Equal(0, outcome.Bytes);

        // Requests the adapter does not support for this source are refused, not thrown.
        var transcode = await Adapter("jxl.cjxl").EncodeAsync(new EncodeRequest(source, EncodeKind.JpegTranscode, null, EffortLevel.Fast, ChromaMode.Auto, 1, dir.File("x.jxl")), Ct);
        Assert.False(transcode.Success);
        Assert.Contains("JpegTranscode", transcode.Error);
        var lossyNoParameter = await Adapter("jxl.cjxl").EncodeAsync(new EncodeRequest(source, EncodeKind.Lossy, null, EffortLevel.Fast, ChromaMode.Auto, 1, dir.File("y.jxl")), Ct);
        Assert.False(lossyNoParameter.Success);
        Assert.False(File.Exists(dir.File("x.jxl")) || File.Exists(dir.File("y.jxl")));
    }

    private static string Skip(string reason)
    {
        Assert.Skip(reason);
        return string.Empty;
    }
}
