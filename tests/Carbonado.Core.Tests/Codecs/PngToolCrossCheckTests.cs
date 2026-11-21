using System.IO.Compression;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>Independent decoders (oxipng, libjxl, libavif) must read Carbonado's PNG/PAM output exactly.</summary>
public class PngToolCrossCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Png_OutputAcceptedByOxipngAndCjxl()
    {
        string oxipng = TestTools.Require(ToolId.Oxipng);
        string cjxl = TestTools.Require(ToolId.Cjxl);
        string djxl = TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("png-tools");
        using var runner = new ProcessRunner();

        var cases = new List<(string Name, PixelBuffer Pixels, PngEncodeOptions? Options)>
        {
            ("rgb-minsum", TestImages.PhotoLike(61, 37), new PngEncodeOptions()),
            ("rgba-interlaced", TestImages.WithAlpha(45, 29), new PngEncodeOptions(Interlace: true, Filter: PngFilterStrategy.Paeth)),
            ("palette-trns", PngCodecTests.Synthetic(PngColorType.Palette, 4, 33, 21, seed: 9), new PngEncodeOptions(PngColorType.Palette, 4, Filter: PngFilterStrategy.Entropy)),
            ("gray2", PngCodecTests.Synthetic(PngColorType.Gray, 2, 30, 17, seed: 3), new PngEncodeOptions(PngColorType.Gray, 2, CompressionLevel.Fastest)),
            ("gray-alpha16", PngCodecTests.Synthetic(PngColorType.GrayAlpha, 16, 20, 13, seed: 4), new PngEncodeOptions(PngColorType.GrayAlpha, 16)),
            ("rgba16", TestImages.Deep16(), new PngEncodeOptions(Filter: PngFilterStrategy.Average)),
            ("fast-parallel", TestImages.PhotoLike(1500, 1100), null),
        };

        try
        {
            foreach (var (name, px, options) in cases)
            {
                string png = dir.File(name + ".png");
                using (var fs = File.Create(png))
                {
                    if (options is null)
                    {
                        PngEncoder.EncodeFast(fs, px, null);
                    }
                    else
                    {
                        PngEncoder.Encode(fs, px, null, options);
                    }
                }

                // oxipng parses, validates and re-encodes; its output must decode to the same pixels.
                var oxi = await runner.RunAsync(new ProcessSpec(oxipng, ["-q", "-o", "1", "--out", name + "-oxi.png", name + ".png"], dir.Path), Ct);
                Assert.True(oxi.Success, $"{name}: oxipng {oxi.ExitCode} {oxi.Stderr}");
                using (var reoptimized = PngTestFiles.DecodeFile(dir.File(name + "-oxi.png")))
                {
                    PixelAssert.Identical(px, reoptimized.Pixels);
                }

                // libjxl reads the PNG losslessly; decode back through PAM.
                var enc = await runner.RunAsync(new ProcessSpec(cjxl, [name + ".png", name + ".jxl", "-d", "0", "-e", "1", "--quiet"], dir.Path), Ct);
                Assert.True(enc.Success, $"{name}: cjxl {enc.ExitCode} {enc.Stderr}");
                var dec = await runner.RunAsync(new ProcessSpec(djxl, [name + ".jxl", name + ".pam", "--quiet"], dir.Path), Ct);
                Assert.True(dec.Success, $"{name}: djxl {dec.ExitCode} {dec.Stderr}");
                using var roundTrip = PngTestFiles.DecodePamFile(dir.File(name + ".pam"));
                PixelAssert.VisiblyIdentical(px, roundTrip.Pixels);
            }
        }
        finally
        {
            foreach (var c in cases)
            {
                c.Pixels.Dispose();
            }
        }
    }

    [Fact]
    public async Task Pam_OutputAcceptedByCjxl()
    {
        string cjxl = TestTools.Require(ToolId.Cjxl);
        string djxl = TestTools.Require(ToolId.Djxl);
        using var dir = new TempDir("pam-tools");
        using var runner = new ProcessRunner();
        foreach (var (name, make, gray) in new (string, Func<PixelBuffer>, bool)[]
        {
            ("alpha", () => TestImages.WithAlpha(40, 30), false),
            ("opaque", () => TestImages.Graphic(40, 30), false),
            ("gray", () => TestImages.Gray(40, 30), true),
            ("deep", () => TestImages.Deep16(), false),
        })
        {
            using var px = make();
            using (var fs = File.Create(dir.File(name + ".pam")))
            {
                PnmCodec.WritePam(fs, px, allowGray: gray);
            }

            Assert.True((await runner.RunAsync(new ProcessSpec(cjxl, [name + ".pam", name + ".jxl", "-d", "0", "-e", "1", "--quiet"], dir.Path), Ct)).Success);
            Assert.True((await runner.RunAsync(new ProcessSpec(djxl, [name + ".jxl", name + "-jxl.pam", "--quiet"], dir.Path), Ct)).Success);
            using var viaJxl = PngTestFiles.DecodePamFile(dir.File(name + "-jxl.pam"));
            PixelAssert.Identical(px, viaJxl.Pixels);
        }
    }

    [Fact]
    public async Task Png_OutputAcceptedByAvifencLossless()
    {
        string avifenc = TestTools.Require(ToolId.Avifenc);
        string avifdec = TestTools.Require(ToolId.Avifdec);
        using var dir = new TempDir("png-avif");
        using var runner = new ProcessRunner();
        using var px = TestImages.WithAlpha(48, 32);
        using (var fs = File.Create(dir.File("in.png")))
        {
            PngEncoder.EncodeFast(fs, px, null);
        }

        var enc = await runner.RunAsync(new ProcessSpec(avifenc, ["-l", "-d", "8", "-s", "9", "in.png", "out.avif"], dir.Path), Ct);
        Assert.True(enc.Success, enc.Stderr);
        var dec = await runner.RunAsync(new ProcessSpec(avifdec, ["out.avif", "back.png"], dir.Path), Ct);
        Assert.True(dec.Success, dec.Stderr);
        using var back = PngTestFiles.DecodeFile(dir.File("back.png"));
        PixelAssert.VisiblyIdentical(px, back.Pixels);
    }
}
