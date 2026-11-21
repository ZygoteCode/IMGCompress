using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Decoding;

/// <summary>Writes one source image in many formats, using WPF encoders and the real tools.</summary>
public static class FormatFixtures
{
    private static readonly Lazy<ToolLocator> s_locator = new(() =>
        new ToolLocator(new ToolLocatorOptions([TestTools.Root ?? "missing"], TestTools.Root is { } r ? Path.Combine(r, "manifest.json") : null, SearchPath: false)));

    public static ToolLocator Locator => s_locator.Value;

    public static ImageLoader Loader(TempDir dir) => new(Locator, ProcessRunner.Shared, dir.Sub("work"));

    /// <summary>16 flat opaque colours in 8×8 blocks: exact in every lossless format, GIF and BMP included.</summary>
    public static PixelBuffer Blocks(int width = 48, int height = 32) => TestImages.Palette(width, height, 16);

    public static async Task<string> WriteAsync(string format, PixelBuffer px, TempDir dir)
    {
        var ct = TestContext.Current.CancellationToken;
        string png = dir.File("source-" + Guid.NewGuid().ToString("N")[..6] + ".png");
        File.WriteAllBytes(png, PngTestFiles.Still(px));
        string target = dir.File($"image-{format}{Extension(format)}");
        switch (format)
        {
            case "png":
                File.Copy(png, target);
                break;
            case "apng":
                File.WriteAllBytes(target, PngTestFiles.Apng(px, px));
                break;
            case "pam":
                using (var fs = File.Create(target))
                {
                    PnmCodec.WritePam(fs, px);
                }

                break;
            case "jpeg":
                TestImages.SaveJpegWithWic(px, target, 95);
                break;
            case "bmp":
                SaveWith(new BmpBitmapEncoder(), new FormatConvertedBitmap(TestImages.ToBitmapSource(px), PixelFormats.Bgr24, null, 0), target);
                break;
            case "gif":
                SaveWith(new GifBitmapEncoder(), Indexed(px), target);
                break;
            case "tiff":
                SaveWith(new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }, new FormatConvertedBitmap(TestImages.ToBitmapSource(px), PixelFormats.Bgr24, null, 0), target);
                break;
            case "jxr":
                SaveWith(new WmpBitmapEncoder { Lossless = true }, new FormatConvertedBitmap(TestImages.ToBitmapSource(px), PixelFormats.Bgr24, null, 0), target);
                break;
            case "ico":
                File.WriteAllBytes(target, Ico(File.ReadAllBytes(png), px.Width, px.Height));
                break;
            case "webp":
                await Run(ToolId.Cwebp, ["-quiet", "-lossless", "-exact", png, "-o", target], ct);
                break;
            case "avif":
                await Run(ToolId.Avifenc, ["-l", "-d", "8", "-s", "9", png, target], ct);
                break;
            case "jxl":
                await Run(ToolId.Cjxl, [png, target, "-d", "0", "-e", "1", "--quiet"], ct);
                break;
            case "qoi" or "tga":
                await Run(ToolId.Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", png, target], ct);
                break;
            case "jp2":
                await Run(ToolId.Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", png, "-c:v", "jpeg2000", "-pred", "1", target], ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }

        return target;
    }

    public static async Task Run(ToolId tool, IReadOnlyList<string> args, CancellationToken ct, string? workDir = null)
    {
        var result = await ProcessRunner.Shared.RunAsync(new ProcessSpec(TestTools.Require(tool), args, workDir), ct);
        Assert.True(result.Success, $"{tool} failed ({result.ExitCode}): {result.Stderr}");
    }

    public static ToolId? ToolFor(string format) => format switch
    {
        "webp" => ToolId.Cwebp,
        "avif" => ToolId.Avifenc,
        "jxl" => ToolId.Cjxl,
        "qoi" or "tga" or "jp2" => ToolId.Ffmpeg,
        _ => null,
    };

    public static string Extension(string format) => format switch
    {
        "jpeg" => ".jpg",
        "tiff" => ".tif",
        "pam" => ".pam",
        _ => "." + format,
    };

    private static void SaveWith(BitmapEncoder encoder, BitmapSource source, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// An animated GIF as real animations are written: exact palette frames, a looping NETSCAPE2.0 extension and a
    /// Graphic Control Extension (100 ms) before every frame. WPF's encoder drops frame delays, so they are inserted here.
    /// </summary>
    public static void AnimatedGif(string path, params PixelBuffer[] frames)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var frame in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(Indexed(frame)));
        }

        using var raw = new MemoryStream();
        encoder.Save(raw);
        byte[] gif = raw.ToArray();
        using var output = new MemoryStream();
        int pos = 13 + ((gif[10] & 0x80) != 0 ? 3 << ((gif[10] & 7) + 1) : 0);
        output.Write(gif, 0, pos);
        output.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        while (pos < gif.Length)
        {
            byte introducer = gif[pos];
            int start = pos;
            if (introducer == 0x3B)
            {
                output.WriteByte(0x3B);
                break;
            }

            if (introducer == 0x2C)
            {
                output.Write([0x21, 0xF9, 0x04, 0x04, 10, 0, 0, 0]);
                byte flags = gif[pos + 9];
                pos += 10 + ((flags & 0x80) != 0 ? 3 << ((flags & 7) + 1) : 0) + 1;
            }
            else
            {
                pos += 2;
            }

            while (gif[pos] != 0)
            {
                pos += gif[pos] + 1;
            }

            pos++;

            // Drop WPF's own extensions (they carry no timing); keep image descriptors and their data.
            if (introducer == 0x2C)
            {
                output.Write(gif, start, pos - start);
            }
        }

        File.WriteAllBytes(path, output.ToArray());
    }

    /// <summary>An exact Indexed8 bitmap (no quantisation) of an image with at most 256 opaque colours.</summary>
    public static BitmapSource Indexed(PixelBuffer px)
    {
        var colors = new List<Color>();
        var index = new Dictionary<Rgba32, byte>();
        var bytes = new byte[px.Width * px.Height];
        for (int y = 0; y < px.Height; y++)
        {
            for (int x = 0; x < px.Width; x++)
            {
                Rgba32 c = px.Row32(y)[x];
                if (!index.TryGetValue(c, out byte i))
                {
                    i = (byte)colors.Count;
                    index[c] = i;
                    colors.Add(Color.FromRgb(c.R, c.G, c.B));
                }

                bytes[(y * px.Width) + x] = i;
            }
        }

        var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Indexed8, new BitmapPalette(colors), bytes, px.Width);
        bmp.Freeze();
        return bmp;
    }

    // Vista-style icon: one directory entry pointing at an embedded PNG.
    private static byte[] Ico(byte[] png, int width, int height)
    {
        var ico = new byte[6 + 16 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        ico[6] = (byte)width;
        ico[7] = (byte)height;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        png.CopyTo(ico, 22);
        return ico;
    }
}
