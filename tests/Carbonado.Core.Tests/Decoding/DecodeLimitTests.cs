using System.Buffers.Binary;
using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Decoding;

/// <summary><see cref="LoadOptions.MaxPixels"/> and the header checks of the WIC and tool decoders.</summary>
[Collection(nameof(MemorySensitive))]
public class DecodeLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LoadOptions_MaxPixels_DefaultsAndValidation()
    {
        Assert.Equal(1L << 28, new LoadOptions().MaxPixels);
        Assert.Equal(LoadOptions.DefaultMaxPixels, new LoadOptions(ApplyOrientation: false).MaxPixels);
        Assert.Equal(1000L, (new LoadOptions() with { MaxPixels = 1000 }).MaxPixels);
        Assert.NotEqual(new LoadOptions(), new LoadOptions { MaxPixels = 5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadOptions { MaxPixels = 0 });
    }

    [Theory]
    [InlineData("png")]
    [InlineData("pam")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    public async Task Loader_RefusesImagesAboveMaxPixels(string format)
    {
        using var dir = new TempDir("limit");
        using var px = FormatFixtures.Blocks(16, 16);
        string path = await FormatFixtures.WriteAsync(format, px, dir);
        var loader = FormatFixtures.Loader(dir);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(path, new LoadOptions { MaxPixels = 255 }, Ct));
        Assert.Contains("exceeds", ex.Message);
        using var ok = await loader.LoadAsync(path, new LoadOptions { MaxPixels = 256 }, Ct);
        Assert.Equal(16, ok.Pixels.Width);
    }

    [Fact]
    public async Task Wic_ForgedJpegHeader_IsRefusedBeforeDecoding()
    {
        // A complete 8×8 JPEG whose frame header claims 60000 × 60000 (10.8 GB for WIC's cached decode).
        using var dir = new TempDir("jpeg");
        using var px = TestImages.PhotoLike(8, 8);
        string path = dir.File("forged.jpg");
        TestImages.SaveJpegWithWic(px, path);
        byte[] jpeg = File.ReadAllBytes(path);
        int sof = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        Assert.True(sof > 0, "WIC writes baseline JPEGs");
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 5), 60000);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), 60000);
        File.WriteAllBytes(path, jpeg);

        var loader = FormatFixtures.Loader(dir);
        InvalidDataException? ex = null;
        var used = await Allocations.MeasureAsync(async () => ex = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(path, new LoadOptions(), Ct)));
        Assert.Contains("exceeds", ex!.Message);
        used.AssertSmall();
    }

    [Fact]
    public async Task Gif_ForgedLogicalScreen_IsRefusedBeforeAllocatingTheCanvas()
    {
        // A 1×1 frame on a declared 65535 × 65535 screen (a 17 GB canvas).
        using var dir = new TempDir("gif");
        using var px = TestImages.Palette(1, 1, 1);
        string path = await FormatFixtures.WriteAsync("gif", px, dir);
        byte[] gif = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 65535);
        File.WriteAllBytes(path, gif);

        var loader = FormatFixtures.Loader(dir);
        InvalidDataException? ex = null;
        var used = await Allocations.MeasureAsync(async () => ex = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(path, new LoadOptions(), Ct)));
        Assert.Contains("exceeds", ex!.Message);
        used.AssertSmall();
    }

    [Fact]
    public async Task WebP_ForgedAnimationCanvas_IsRefusedBeforeDecoding()
    {
        TestTools.Require(ToolId.Dwebp);
        using var dir = new TempDir("webp");
        using var px = TestImages.Palette(2, 2, 2);
        byte[] still = File.ReadAllBytes(await FormatFixtures.WriteAsync("webp", px, dir));
        Assert.True(still.AsSpan(12, 4).SequenceEqual("VP8L"u8), "cwebp -lossless writes a simple VP8L file");
        int vp8lSize = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(still.AsSpan(16));
        byte[] vp8l = still.AsSpan(12, vp8lSize + (vp8lSize & 1)).ToArray();

        // A valid 2×2 first frame on a declared 20000 × 20000 canvas (1.6 GB once placed).
        using var ms = new MemoryStream();
        ms.Write("RIFF\0\0\0\0WEBP"u8);
        Chunk(ms, "VP8X", [0x02, 0, 0, 0, .. U24(19999), .. U24(19999)]);
        Chunk(ms, "ANIM", [0, 0, 0, 0, 0, 0]);
        Chunk(ms, "ANMF", [.. U24(0), .. U24(0), .. U24(1), .. U24(1), .. U24(100), 0, .. vp8l]);
        byte[] forged = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(4), (uint)(forged.Length - 8));
        string path = dir.File("forged.webp");
        File.WriteAllBytes(path, forged);

        var loader = FormatFixtures.Loader(dir);
        InvalidDataException? ex = null;
        var used = await Allocations.MeasureAsync(async () => ex = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(path, new LoadOptions(), Ct)));
        Assert.Contains("exceeds", ex!.Message);
        used.AssertSmall();

        // A metadata chunk whose size runs past the end of the file is a truncation, not a 60 MB allocation.
        using var truncated = new MemoryStream();
        truncated.Write("RIFF\0\0\0\0WEBP"u8);
        Chunk(truncated, "VP8X", [0x20, 0, 0, 0, .. U24(0), .. U24(0)]);
        truncated.Write([.. "ICCP"u8, .. BitConverter.GetBytes(60u << 20), 1, 2, 3]);
        File.WriteAllBytes(path, truncated.ToArray());
        (await Allocations.MeasureAsync(() => Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(path, new LoadOptions(), Ct)))).AssertSmall();
    }

    [Fact]
    public void Ffmpeg_InputSize_IsReadFromTheProbe()
    {
        const string Probe = "Input #0, image2, from 'in.qoi':\n  Duration: 00:00:00.04, start: 0.000000, bitrate: N/A\n"
            + "  Stream #0:0: Video: qoi, rgba, 100000x80000, 25 fps, 25 tbr, 25 tbn\nAt least one output file must be specified\n";
        Assert.Equal((100000, 80000), ToolDecoders.ParseFfmpegInputSize(Probe));
        Assert.Equal((100000, 80000), ToolDecoders.ParseFfmpegInputSize(Probe + "Output #0, rawvideo, to 'o.raw':\n  Stream #0:0: Video: rawvideo, rgba, 64x64\n"));
        Assert.Equal((0, 0), ToolDecoders.ParseFfmpegInputSize("in.xyz: Invalid data found when processing input\n"));
        Assert.Equal((0, 0), ToolDecoders.ParseFfmpegInputSize("Input #0, wav, from 'a.wav':\n  Stream #0:0: Audio: pcm_s16le\nOutput #0, null:\n  Stream #0:0: Video: rawvideo, 640x480\n"));
    }

    private static byte[] U24(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];

    private static void Chunk(Stream s, string id, byte[] payload)
    {
        s.Write(System.Text.Encoding.ASCII.GetBytes(id));
        s.Write(BitConverter.GetBytes((uint)payload.Length));
        s.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            s.WriteByte(0);
        }
    }
}
