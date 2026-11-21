using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
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

/// <summary>
/// The prepared source's lifetime under concurrency, the metadata policy for animations, and adapter failures that
/// must become outcomes.
/// </summary>
public class SourceLifetimeAndPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreparedSource_DisposeDuringAnInFlightWrite_WaitsForIt_ThenCleansUp()
    {
        using var dir = new TempDir("dispose-race");
        using var px = TestImages.PhotoLike(64, 48);
        var source = Source(dir, px);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pixelReads = 0;
        string? written = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // A write that reads the pixels late and ignores cancellation (like the built-in encoders).
        var request = source.GetDerivedAsync("slow.bin", async (path, _) =>
        {
            started.SetResult();
            await release.Task;
            byte first = source.Pixels.RowBytes(0)[0];
            Interlocked.Increment(ref pixelReads);
            await File.WriteAllBytesAsync(path, [first], CancellationToken.None);
            written = path;
        }, cts.Token);
        await started.Task.WaitAsync(Ct);

        // The caller stops waiting (its job was cancelled); the shared write goes on.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // Dispose waits for the write instead of freeing the pixels under it, and refuses new requests at once.
        var disposing = Task.Run(source.Dispose, Ct);
        await Task.Delay(300, Ct);
        Assert.False(disposing.IsCompleted, "Dispose returned while a write was still using the pixels");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetPngAsync(Ct));

        release.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(1, pixelReads);
        Assert.NotNull(written);
        Assert.False(File.Exists(written), "the write that finished during Dispose left its file behind");
        Assert.Empty(Directory.EnumerateFiles(source.WorkDir));
        Assert.True(source.Pixels.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetDerivedAsync("late.bin", (_, _) => Task.CompletedTask, Ct));
    }

    [Fact]
    public async Task PreparedSource_Dispose_CancelsDerivedCreations()
    {
        using var dir = new TempDir("dispose-cancel");
        using var px = TestImages.PhotoLike(16, 16);
        var source = Source(dir, px);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A derived intermediate produced by a long tool run that only the source's lifetime can stop.
        var request = source.GetDerivedAsync("tool-output.bin", async (path, lifetime) =>
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3], CancellationToken.None);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, lifetime);
        }, Ct);
        await started.Task.WaitAsync(Ct);

        var clock = Stopwatch.StartNew();
        source.Dispose();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Empty(Directory.EnumerateFiles(source.WorkDir));
    }

    [Fact]
    public async Task AnimatedPng_MetadataFollowsThePolicy()
    {
        TestTools.Require(ToolId.Oxipng);
        using var dir = new TempDir("apng-policy");
        using var a = FormatFixtures.Blocks();
        using var b = TestImages.Palette(48, 32, 7);
        string original = dir.File("anim.png");
        File.WriteAllBytes(original, CameraApng(a, b));

        // Optimized as is, the original would keep all of it (GPS included).
        string unfiltered = dir.File("unfiltered.png");
        await FormatFixtures.Run(ToolId.Oxipng, ["-o", "1", "-q", "--out", unfiltered, original], Ct);
        Assert.True(ExifReader.HasGps(PngExif(unfiltered)), "fixture: oxipng keeps EXIF unless told otherwise");

        // EXIF kept without GPS (orientation 1: prepared pixels are upright), and a policy keeping nothing at all.
        foreach (var policy in new[] { new ImageMetadata { Exif = ExifTestData.Build(false, 1, withGps: false) }, new ImageMetadata() })
        {
            using var source = Source(dir, a, policy, originalPath: original, format: ImageFormat.Apng, frames: 2);
            string output = dir.File($"out-{Guid.NewGuid():N}.png");
            var outcome = await Adapter("png.oxipng").EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Fast, ChromaMode.Auto, 1, output), Ct);
            Assert.True(outcome.Success, outcome.Error);

            var types = PngTestFiles.Chunks(File.ReadAllBytes(output)).Select(c => c.Type).ToList();
            Assert.Contains("acTL", types);
            Assert.Equal(2, types.Count(t => t == "fcTL"));
            Assert.DoesNotContain("tEXt", types);
            Assert.DoesNotContain("tIME", types);
            Assert.DoesNotContain("prVt", types);

            // The frames are stored unrotated, so their orientation travels with them (alone when EXIF is not kept).
            byte[]? exif = PngExif(output);
            Assert.NotNull(exif);
            Assert.False(ExifReader.HasGps(exif));
            Assert.Equal(6, ExifReader.ReadOrientation(exif));
            Assert.Equal(policy.Exif is not null, Encoding.ASCII.GetString(exif).Contains("Canon", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AnimatedPng_ToJxl_UsesThePolicyFilteredFrames()
    {
        TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("apng-jxl");
        using var a = FormatFixtures.Blocks();
        using var b = TestImages.Palette(48, 32, 7);
        string original = dir.File("anim.png");
        File.WriteAllBytes(original, CameraApng(a, b));

        using var source = Source(dir, a, new ImageMetadata { Exif = ExifTestData.Build(false, 1, withGps: false) }, originalPath: original, format: ImageFormat.Apng, frames: 2);
        string output = dir.File("anim.jxl");
        var outcome = await Adapter("jxl.cjxl").EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Fast, ChromaMode.Auto, 1, output), Ct);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("original-filtered.png", outcome.CommandLine);

        using var reloaded = await FormatFixtures.Loader(dir).LoadAsync(output, new LoadOptions(), Ct);
        Assert.True(reloaded.FrameCount >= 2);
        Assert.False(reloaded.Metadata.Exif is { } exif && ExifReader.HasGps(exif), "GPS survived the policy");
    }

    [Fact]
    public async Task AnimatedGif_MetadataFollowsThePolicy()
    {
        TestTools.Require(ToolId.Gif2webp);
        using var dir = new TempDir("gif-policy");
        using var a = FormatFixtures.Blocks();
        using var b = TestImages.Palette(48, 32, 7);
        string gif = dir.File("anim.gif");
        FormatFixtures.AnimatedGif(gif, a, b);
        byte[] gifXmp = Encoding.UTF8.GetBytes("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">secret</x:xmpmeta><?xpacket end=\"w\"?>");
        File.WriteAllBytes(gif, WithXmpExtension(File.ReadAllBytes(gif), gifXmp));

        // gif2webp's default copies the GIF's XMP packet.
        string unfiltered = dir.File("unfiltered.webp");
        await FormatFixtures.Run(ToolId.Gif2webp, ["-quiet", gif, "-o", unfiltered], Ct);
        Assert.True(RiffChunks(unfiltered).ContainsKey("XMP "), "fixture: gif2webp copies GIF XMP by default");

        foreach (byte[]? kept in new[] { null, ExifTestData.Xmp })
        {
            using var source = Source(dir, a, new ImageMetadata { Xmp = kept }, originalPath: gif, format: ImageFormat.Gif, frames: 2);
            string output = dir.File($"out-{Guid.NewGuid():N}.webp");
            var outcome = await Adapter("webp.gif2webp").EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Fast, ChromaMode.Auto, 1, output), Ct);
            Assert.True(outcome.Success, outcome.Error);
            var chunks = RiffChunks(output);
            Assert.True(chunks.ContainsKey("ANIM") && chunks.ContainsKey("ANMF"), "the animation was lost");
            Assert.Equal(kept, chunks.GetValueOrDefault("XMP "));
        }
    }

    [Fact]
    public async Task Adapter_ToolThatCannotStart_OrADisposedRunner_IsAnErrorOutcome()
    {
        using var dir = new TempDir("bad-tool");
        var (package, exe) = ToolLocator.Layout(ToolId.Cjxl);
        string root = dir.Sub("tools");
        Directory.CreateDirectory(Path.Combine(root, package));
        File.WriteAllText(Path.Combine(root, package, exe), "not a program");
        var tools = new ToolLocator(new ToolLocatorOptions([root], VerifyHashes: false, SearchPath: false));
        using var px = TestImages.PhotoLike(16, 16);
        using var source = Source(dir, px);
        string output = dir.File("out.jxl");
        var request = new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Fast, ChromaMode.Auto, 1, output);

        var broken = await Adapter("jxl.cjxl", tools).EncodeAsync(request, Ct);
        Assert.False(broken.Success);
        Assert.Contains("Could not start", broken.Error);
        Assert.False(File.Exists(output));

        var runner = new ProcessRunner();
        runner.Dispose();
        var stopped = await Adapter("jxl.cjxl", tools, runner).EncodeAsync(request, Ct);
        Assert.False(stopped.Success);
        Assert.False(string.IsNullOrEmpty(stopped.Error));
        Assert.Empty(Directory.EnumerateFiles(source.WorkDir, "out-*"));
    }

    [Fact]
    public async Task Adapter_NeverWritesOverTheSourceFile()
    {
        using var dir = new TempDir("overwrite");
        using var px = TestImages.Graphic(32, 32);
        using var source = Source(dir, px);
        byte[] before = File.ReadAllBytes(source.OriginalPath);
        foreach (string target in new[] { source.OriginalPath, source.OriginalPath.ToUpperInvariant() })
        {
            var outcome = await Adapter("png.builtin").EncodeAsync(new EncodeRequest(source, EncodeKind.Lossless, null, EffortLevel.Fast, ChromaMode.Auto, 1, target), Ct);
            Assert.False(outcome.Success);
            Assert.Contains("source file", outcome.Error);
        }

        Assert.Equal(before, File.ReadAllBytes(source.OriginalPath));
    }

    [Fact]
    public async Task Verifier_WithWorkDir_LeavesTheOutputFolderAlone_AndAcceptsUnicodeNames()
    {
        TestTools.Require(ToolId.Cjxl);
        TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("verify-work");
        using var px = TestImages.PhotoLike(64, 48);
        string jpeg = dir.File("photo.jpg");
        TestImages.SaveJpegWithWic(px, jpeg, 90);
        using var source = Source(dir, px, originalPath: jpeg, format: ImageFormat.Jpeg);
        string outputs = dir.Sub("résultats ü");
        string output = Path.Combine(outputs, "フォト.jxl");
        var outcome = await Adapter("jxl.cjxl").EncodeAsync(new EncodeRequest(source, EncodeKind.JpegTranscode, null, EffortLevel.Fast, ChromaMode.Auto, 1, output), Ct);
        Assert.True(outcome.Success, outcome.Error);

        string work = dir.Sub("session-temp");
        var verifier = new OutputVerifier(FormatFixtures.Loader(dir), Locator, ProcessRunner.Shared, work);
        Assert.True(await verifier.VerifyJpegReconstructionAsync(output, await source.GetJpegReferenceAsync(Ct), Ct));
        Assert.Equal([output], Directory.GetFiles(outputs));
        Assert.Empty(Directory.GetFiles(work));
    }

    // A two-frame APNG as a phone app might write it: EXIF (orientation 6, GPS), a comment, a timestamp, a private chunk.
    private static byte[] CameraApng(PixelBuffer a, PixelBuffer b)
    {
        byte[] apng = PngTestFiles.Apng(a, b);
        using var ms = new MemoryStream();
        ms.Write(apng, 0, 33);
        PngTestFiles.WriteChunk(ms, "eXIf", ExifTestData.Build(false, 6, withGps: true));
        PngTestFiles.WriteChunk(ms, "tEXt", "Comment\0taken at home"u8);
        PngTestFiles.WriteChunk(ms, "tIME", [0x07, 0xEA, 11, 21, 12, 0, 0]);
        PngTestFiles.WriteChunk(ms, "prVt", [1, 2, 3]);
        ms.Write(apng, 33, apng.Length - 33);
        return ms.ToArray();
    }

    private static byte[]? PngExif(string path)
    {
        using var fs = File.OpenRead(path);
        return PngDecoder.ReadMetadata(fs).Exif;
    }

    // The XMP application extension (XMP spec part 3): "XMP DataXMP", the raw packet, then the 258-byte magic trailer.
    private static byte[] WithXmpExtension(byte[] gif, byte[] xmp)
    {
        int pos = 13 + ((gif[10] & 0x80) != 0 ? 3 << ((gif[10] & 7) + 1) : 0);
        byte[] trailer = [0x01, .. Enumerable.Range(0, 256).Select(i => (byte)(255 - i)), 0x00];
        return [.. gif.AsSpan(0, pos), 0x21, 0xFF, 0x0B, .. "XMP DataXMP"u8, .. xmp, .. trailer, .. gif.AsSpan(pos)];
    }

    // Top-level RIFF chunks of a WebP file and their payloads.
    private static Dictionary<string, byte[]> RiffChunks(string path)
    {
        byte[] webp = File.ReadAllBytes(path);
        var chunks = new Dictionary<string, byte[]>();
        for (int pos = 12; pos + 8 <= webp.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(pos + 4));
            chunks.TryAdd(Encoding.ASCII.GetString(webp, pos, 4), webp.AsSpan(pos + 8, size).ToArray());
            pos += 8 + size + (size & 1);
        }

        return chunks;
    }
}
