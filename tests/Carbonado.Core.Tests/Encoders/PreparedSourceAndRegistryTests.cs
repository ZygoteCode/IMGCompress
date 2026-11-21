using Carbonado.Core.Codecs;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;
using static Carbonado.Core.Tests.Encoders.EncoderTestSupport;

namespace Carbonado.Core.Tests.Encoders;

public class PreparedSourceAndRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreparedSource_WritesIntermediatesOnce()
    {
        using var dir = new TempDir("prepared");
        using var px = TestImages.WithAlpha(200, 150);
        var metadata = new ImageMetadata { Icc = TestIcc.DisplayP3Gamma22(), Exif = ExifTestData.Build(false, 1, withGps: false) };
        var source = Source(dir, px, metadata);
        string original = source.OriginalPath;
        byte[] originalBytes = File.ReadAllBytes(original);

        // Many concurrent requests: each intermediate is created exactly once and every caller gets the same path.
        var pngs = Enumerable.Range(0, 16).Select(_ => Task.Run(() => source.GetPngAsync(Ct), Ct)).ToArray();
        var pams = Enumerable.Range(0, 16).Select(_ => Task.Run(() => source.GetPamAsync(Ct), Ct)).ToArray();
        string[] pngPaths = await Task.WhenAll(pngs);
        string[] pamPaths = await Task.WhenAll(pams);
        Assert.Single(pngPaths.Distinct());
        Assert.Single(pamPaths.Distinct());
        Assert.Equal(Path.Combine(source.WorkDir, PreparedSource.PngName), pngPaths[0]);
        DateTime written = File.GetLastWriteTimeUtc(pngPaths[0]);

        Assert.Equal(pngPaths[0], await source.GetPngAsync(Ct));
        Assert.Equal(written, File.GetLastWriteTimeUtc(pngPaths[0]));
        Assert.Equal(2, source.IntermediatesCreated);

        // Intermediates are exact and carry the metadata (PNG) or nothing (PAM).
        using (var png = PngTestFiles.DecodeFile(pngPaths[0]))
        {
            PixelAssert.Identical(px, png.Pixels);
            Assert.Equal(metadata.Icc, png.Metadata.Icc);
            Assert.Equal(metadata.Exif, png.Metadata.Exif);
        }

        using (var pam = PngTestFiles.DecodePamFile(pamPaths[0]))
        {
            PixelAssert.Identical(px, pam.Pixels);
        }

        string? icc = await source.GetIccPathAsync();
        Assert.Equal(metadata.Icc, File.ReadAllBytes(icc!));
        Assert.Null(await source.GetXmpPathAsync());
        Assert.Equal(3, source.IntermediatesCreated);

        // Dispose removes what it created and never touches the original.
        source.Dispose();
        Assert.False(File.Exists(pngPaths[0]));
        Assert.False(File.Exists(pamPaths[0]));
        Assert.False(File.Exists(icc));
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetPngAsync(Ct));
    }

    [Fact]
    public async Task PreparedSource_FailedCreationIsRetried()
    {
        using var dir = new TempDir("prepared");
        using var px = TestImages.Gray(16, 16);
        using var source = Source(dir, px);
        int attempts = 0;
        Task Create(string path, CancellationToken _)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("disk hiccup");
            }

            File.WriteAllText(path, "ok");
            return Task.CompletedTask;
        }

        await Assert.ThrowsAsync<IOException>(() => source.GetDerivedAsync("derived.txt", Create, Ct));
        Assert.False(File.Exists(Path.Combine(source.WorkDir, "derived.txt")));
        string path = await source.GetDerivedAsync("derived.txt", Create, Ct);
        Assert.Equal("ok", File.ReadAllText(path));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PreparedSource_JpegReference_FollowsThePolicy()
    {
        using var dir = new TempDir("prepared");
        using var px = TestImages.PhotoLike(32, 24);
        TestImages.SaveJpegWithWic(px, dir.File("plain.jpg"));
        byte[] plain = File.ReadAllBytes(dir.File("plain.jpg"));

        // A comment segment (private text) right after SOI.
        byte[] comment = [0xFF, 0xFE, 0x00, 0x07, (byte)'s', (byte)'e', (byte)'c', (byte)'r', (byte)'t'];
        string withComment = dir.File("comment.jpg");
        File.WriteAllBytes(withComment, [.. plain[..2], .. comment, .. plain[2..]]);

        // No metadata kept: the comment must not survive, so a filtered copy is the reference.
        using (var strip = Source(dir, px, new ImageMetadata(), originalPath: withComment, format: ImageFormat.Jpeg))
        {
            string reference = await strip.GetJpegReferenceAsync(Ct);
            Assert.NotEqual(withComment, reference);
            Assert.True(File.ReadAllBytes(reference).AsSpan().IndexOf(comment) < 0);
            await using var fs = File.OpenRead(reference);
            Assert.False(JpegSegments.Read(fs).HasOtherMetadata);
        }

        // Keeping EXIF keeps the other descriptive segments: nothing changes, the original is the reference.
        byte[] exif = ExifTestData.Build(false, 1, withGps: false);
        string tagged = dir.File("tagged.jpg");
        File.WriteAllBytes(tagged, JpegSegments.Rewrite(File.ReadAllBytes(withComment), exif, null, null, keepOtherMetadata: true));
        using (var keep = Source(dir, px, new ImageMetadata { Exif = exif }, originalPath: tagged, format: ImageFormat.Jpeg))
        {
            Assert.Equal(tagged, await keep.GetJpegReferenceAsync(Ct));
        }

        // A plain JPEG with nothing to remove is its own reference.
        using var clean = Source(dir, px, new ImageMetadata(), originalPath: dir.File("plain.jpg"), format: ImageFormat.Jpeg);
        Assert.Equal(dir.File("plain.jpg"), await clean.GetJpegReferenceAsync(Ct));
    }

    [Fact]
    public void Registry_UsesPrimaryEncodersWhenInstalled()
    {
        TestTools.Require(ToolId.Cjxl);
        var registry = new EncoderRegistry(Locator, ProcessRunner.Shared);
        string[] ids = [.. registry.Available.Select(a => a.Id)];
        Assert.Equal(["jxl.cjxl", "avif.avifenc", "webp.cwebp", "jpeg.sjpeg", "png.oxipng", "png.ect", "png.pngquant", "webp.gif2webp"], ids);
        Assert.Equal(["png.oxipng", "png.ect"], registry.For(OutputFormat.Png, EncodeKind.Lossless).Select(a => a.Id));
        Assert.Equal(["png.pngquant"], registry.For(OutputFormat.Png, EncodeKind.Lossy).Select(a => a.Id));
        Assert.Equal(["jxl.cjxl"], registry.For(OutputFormat.Jxl, EncodeKind.JpegTranscode).Select(a => a.Id));
        Assert.Equal(["webp.cwebp", "webp.gif2webp"], registry.For(OutputFormat.WebP, EncodeKind.Lossless).Select(a => a.Id));
        Assert.Empty(registry.For(OutputFormat.Gif, EncodeKind.Lossy));
        Assert.NotNull(registry.Find("jpeg.sjpeg"));
        Assert.Null(registry.Find("jxl.ffmpeg"));
    }

    [Fact]
    public async Task Registry_FallsBackToFfmpegWhenPrimaryMissing()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        TestTools.Require(ToolId.Djxl);
        TestTools.Require(ToolId.Dwebp);
        TestTools.Require(ToolId.Avifdec);
        using var dir = new TempDir("registry");

        // Only ffmpeg is installed (found in a legacy folder): every format falls back to it, PNG to the built-in optimizer.
        var onlyFfmpeg = new ToolLocator(new ToolLocatorOptions([dir.Sub("empty-tools")], LegacyDirs: [Path.GetDirectoryName(ffmpeg)!], SearchPath: false));
        var registry = new EncoderRegistry(onlyFfmpeg, ProcessRunner.Shared);
        Assert.Equal(["png.builtin", "jxl.ffmpeg", "avif.ffmpeg", "webp.ffmpeg"], registry.Available.Select(a => a.Id));
        Assert.Equal("jxl.ffmpeg", Assert.Single(registry.For(OutputFormat.Jxl, EncodeKind.Lossy)).Id);
        Assert.Equal("avif.ffmpeg", Assert.Single(registry.For(OutputFormat.Avif, EncodeKind.Lossy)).Id);
        Assert.Empty(registry.For(OutputFormat.Avif, EncodeKind.Lossless));
        Assert.Equal("png.builtin", Assert.Single(registry.For(OutputFormat.Png, EncodeKind.Lossless)).Id);
        Assert.Empty(registry.For(OutputFormat.Jpeg, EncodeKind.Lossy));

        // The fallbacks really encode (decoding uses the full tool set).
        using var px = TestImages.PhotoLike(64, 48);
        using var source = Source(dir, px);
        var loader = FormatFixtures.Loader(dir);
        foreach (var adapter in registry.Available.Where(a => a.Id.EndsWith(".ffmpeg", StringComparison.Ordinal)))
        {
            string output = dir.File(adapter.Id + FormatInfo.Extension(adapter.Format));
            var outcome = await adapter.EncodeAsync(new EncodeRequest(source, EncodeKind.Lossy, adapter.InitialGuess(85, source.Analysis), EffortLevel.Fast, ChromaMode.Auto, 2, output), Ct);
            Assert.True(outcome.Success, $"{adapter.Id}: {outcome.Error}");
            using var decoded = await loader.DecodeOutputAsync(output, adapter.Format, Ct);
            Assert.InRange(PixelAssert.MeanDifference8(px, decoded), 0, 14);
        }

        // Nothing installed at all: only the built-in PNG optimizer remains.
        var nothing = new EncoderRegistry(new ToolLocator(new ToolLocatorOptions([dir.Sub("none")], SearchPath: false)), ProcessRunner.Shared);
        Assert.Equal(["png.builtin"], nothing.Available.Select(a => a.Id));
    }

    [Fact]
    public async Task Verifier_RejectsNonTranscodedJxl()
    {
        TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("verify");
        using var px = TestImages.PhotoLike(48, 32);
        TestImages.SaveJpegWithWic(px, dir.File("a.jpg"));
        using var source = Source(dir, px);
        var cjxl = Adapter("jxl.cjxl");
        var outcome = await cjxl.EncodeAsync(new EncodeRequest(source, EncodeKind.Lossy, 1, EffortLevel.Fast, ChromaMode.Auto, 1, dir.File("pixels.jxl")), Ct);
        Assert.True(outcome.Success, outcome.Error);
        var verifier = new OutputVerifier(FormatFixtures.Loader(dir), Locator, ProcessRunner.Shared);

        // A pixel-encoded JXL has no JPEG reconstruction data: -J fails, so verification is false (never an exception).
        Assert.False(await verifier.VerifyJpegReconstructionAsync(dir.File("pixels.jxl"), dir.File("a.jpg"), Ct));
        Assert.Same(verifier.Loader, verifier.Loader);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "reconstructed-*"));
    }
}
