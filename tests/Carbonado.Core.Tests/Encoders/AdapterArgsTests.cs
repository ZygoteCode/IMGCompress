using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;
using static Carbonado.Core.Tests.Encoders.EncoderTestSupport;

namespace Carbonado.Core.Tests.Encoders;

/// <summary>Exact command lines of every adapter, for each effort and kind (no tool runs).</summary>
public class AdapterArgsTests
{
    private static readonly EffortLevel[] Efforts = [EffortLevel.Fast, EffortLevel.Balanced, EffortLevel.Max, EffortLevel.Insane];

    [Fact]
    public void Args_Cjxl()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        using var tagged = Source(dir, TestImages.PhotoLike(8, 8), FullMetadata());
        using var alpha = Source(dir, TestImages.WithAlpha(8, 8));
        var a = Adapter("jxl.cjxl");
        string[] lossyEffort = ["5", "7", "9", "10"], losslessEffort = ["7", "9", "10", "10"], transcodeEffort = ["7", "9", "9", "10"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(["in.pam", "out.jxl", "-d", "1.25", "-e", lossyEffort[i], "--num_threads=4", "--quiet"], Args(a, plain, EncodeKind.Lossy, 1.25, Efforts[i], "in.pam", "out.jxl"));
            string[] extra = Efforts[i] == EffortLevel.Insane ? ["-I", "100", "-g", "3"] : [];
            Assert.Equal(["in.pam", "out.jxl", "-d", "0", "-e", losslessEffort[i], .. extra, "--num_threads=4", "--quiet"], Args(a, plain, EncodeKind.Lossless, null, Efforts[i], "in.pam", "out.jxl"));
            Assert.Equal(["in.pam", "out.jxl", "-d", "0", "-e", losslessEffort[i], .. extra, "--keep_invisible=0", "--num_threads=4", "--quiet"], Args(a, alpha, EncodeKind.Lossless, null, Efforts[i], "in.pam", "out.jxl"));
            Assert.Equal(["in.jpg", "out.jxl", "--lossless_jpeg=1", "-e", transcodeEffort[i], "--num_threads=4", "--quiet"], Args(a, plain, EncodeKind.JpegTranscode, null, Efforts[i], "in.jpg", "out.jxl"));
        }

        Assert.Equal(
            ["in.pam", "out.jxl", "-d", "2", "-e", "7", "--num_threads=1", "-x", "icc_pathname=src.icc", "-x", "exif=src.exif", "-x", "xmp=src.xmp", "--quiet"],
            Args(a, tagged, EncodeKind.Lossy, 2.004, EffortLevel.Balanced, "in.pam", "out.jxl", threads: 1));

        // Distances are rounded to the 0.01 resolution and clamped.
        Assert.Contains("0.05", Args(a, plain, EncodeKind.Lossy, 0.001, EffortLevel.Fast, "in.pam", "out.jxl"));
        Assert.Contains("15", Args(a, plain, EncodeKind.Lossy, 99, EffortLevel.Fast, "in.pam", "out.jxl"));
    }

    [Fact]
    public void Args_Avifenc()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        using var gray = Source(dir, TestImages.Gray(8, 8));
        using var tagged = Source(dir, TestImages.PhotoLike(8, 8), FullMetadata());
        var a = Adapter("avif.avifenc");
        string[] speed = ["8", "6", "3", "1"];
        string[] ignore = ["--ignore-icc", "--ignore-exif", "--ignore-xmp"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(["-q", "63", "-s", speed[i], "-d", "10", "-y", "444", "-j", "4", .. ignore, "--", "in.png", "out.avif"], Args(a, plain, EncodeKind.Lossy, 63, Efforts[i], "in.png", "out.avif"));
            Assert.Equal(["-l", "-d", "8", "-s", speed[i], "-j", "4", .. ignore, "--", "in.png", "out.avif"], Args(a, plain, EncodeKind.Lossless, null, Efforts[i], "in.png", "out.avif"));
        }

        Assert.Equal(["-q", "40", "-s", "6", "-d", "10", "-y", "420", "--sharpyuv", "-j", "4", .. ignore, "--", "in.png", "out.avif"], Args(a, plain, EncodeKind.Lossy, 40, EffortLevel.Balanced, "in.png", "out.avif"));
        Assert.Contains("444", Args(a, plain, EncodeKind.Lossy, 40, EffortLevel.Balanced, "in.png", "out.avif", chroma: ChromaMode.Yuv444));
        Assert.Contains("--sharpyuv", Args(a, plain, EncodeKind.Lossy, 90, EffortLevel.Balanced, "in.png", "out.avif", chroma: ChromaMode.Yuv420));
        Assert.Contains("400", Args(a, gray, EncodeKind.Lossy, 70, EffortLevel.Balanced, "in.png", "out.avif"));
        Assert.Equal(["-q", "70", "-s", "6", "-d", "10", "-y", "444", "-j", "2", "--icc", "src.icc", "--exif", "src.exif", "--xmp", "src.xmp", "--", "in.png", "out.avif"], Args(a, tagged, EncodeKind.Lossy, 70, EffortLevel.Balanced, "in.png", "out.avif", threads: 2));
    }

    [Fact]
    public void Args_Cwebp()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        using var alpha = Source(dir, TestImages.WithAlpha(8, 8), preserveInvisible: true);
        var a = Adapter("webp.cwebp");
        string[] method = ["4", "6", "6", "6"], z = ["6", "9", "9", "9"];
        for (int i = 0; i < 4; i++)
        {
            string[] pass = Efforts[i] >= EffortLevel.Max ? ["-pass", "10"] : [];
            Assert.Equal(["-q", "80", "-m", method[i], "-sharp_yuv", "-af", .. pass, "-mt", "-metadata", "none", "-quiet", "in.png", "-o", "out.webp"], Args(a, plain, EncodeKind.Lossy, 80, Efforts[i], "in.png", "out.webp"));
            Assert.Equal(["-lossless", "-z", z[i], "-mt", "-metadata", "none", "-quiet", "in.png", "-o", "out.webp"], Args(a, plain, EncodeKind.Lossless, null, Efforts[i], "in.png", "out.webp"));
        }

        Assert.Equal(["-q", "80", "-m", "6", "-sharp_yuv", "-af", "-alpha_q", "100", "-exact", "-metadata", "none", "-quiet", "in.png", "-o", "out.webp"], Args(a, alpha, EncodeKind.Lossy, 80, EffortLevel.Balanced, "in.png", "out.webp", threads: 1));
        Assert.Equal(["-lossless", "-z", "9", "-exact", "-metadata", "none", "-quiet", "in.png", "-o", "out.webp"], Args(a, alpha, EncodeKind.Lossless, null, EffortLevel.Max, "in.png", "out.webp", threads: 1));
    }

    [Fact]
    public void Args_Sjpeg()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        var a = Adapter("jpeg.sjpeg");
        string[] ect = ["-1", "-3", "-6", "-9"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(["in.jxl", "out.jpg", "--use_sjpeg", "-q", "85", "--num_threads=4", "--quiet"], Args(a, plain, EncodeKind.Lossy, 85, Efforts[i], "in.jxl", "out.jpg"));
            Assert.Equal(["-quiet", "-progressive", ect[i], "out.jpg"], Args(a, plain, EncodeKind.Optimize, null, Efforts[i], "in.jpg", "out.jpg"));
        }
    }

    [Fact]
    public void Args_PngOptimizers()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        using var iccOnly = Source(dir, TestImages.PhotoLike(8, 8), new ImageMetadata { Icc = PngCodecTests.SyntheticIcc("RGB ") });
        using var all = Source(dir, TestImages.PhotoLike(8, 8), FullMetadata());
        using var alpha = Source(dir, TestImages.WithAlpha(8, 8));
        using var exact = Source(dir, TestImages.WithAlpha(8, 8), preserveInvisible: true);
        var oxipng = Adapter("png.oxipng");
        var ect = Adapter("png.ect");
        string[] level = ["2", "4", "6", "max"], ectLevel = ["-3", "-5", "-9", "-9"];
        for (int i = 0; i < 4; i++)
        {
            string[] zopfli = Efforts[i] == EffortLevel.Insane ? ["-z"] : [];
            string[] brute = Efforts[i] == EffortLevel.Insane ? ["--allfilters-b"] : [];
            Assert.Equal(["-o", level[i], "--strip", "all", .. zopfli, "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, plain, EncodeKind.Lossless, null, Efforts[i], "in.png", "out.png"));
            Assert.Equal(["-quiet", ectLevel[i], "-strip", .. brute, "out.png"], Args(ect, plain, EncodeKind.Lossless, null, Efforts[i], "in.png", "out.png"));
        }

        Assert.Equal(["-o", "4", "--strip", "safe", "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, iccOnly, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Equal(["-o", "4", "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, all, EncodeKind.Optimize, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Equal(["-o", "4", "--strip", "all", "-a", "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, alpha, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Equal(["-o", "4", "--strip", "all", "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, exact, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Equal(["-quiet", "-5", "out.png"], Args(ect, iccOnly, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Equal(["-quiet", "-5", "-strip", "--strict", "out.png"], Args(ect, exact, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
        Assert.Empty(Args(Adapter("png.builtin"), plain, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));

        // An animation's input already carries exactly the policy metadata (and the stored orientation): nothing is stripped.
        using var animated = Source(dir, TestImages.PhotoLike(8, 8), format: ImageFormat.Apng, frames: 2);
        Assert.Equal(["-o", "4", "-t", "4", "-q", "--out", "out.png", "in.png"], Args(oxipng, animated, EncodeKind.Lossless, null, EffortLevel.Balanced, "in.png", "out.png"));
    }

    [Fact]
    public void Args_Pngquant()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        var a = Adapter("png.pngquant");
        string[] speed = ["3", "1", "1", "1"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(["--quality", "0-70", "--speed", speed[i], "--strip", "--force", "--output", "out.png", "--", "in.png"], Args(a, plain, EncodeKind.Lossy, 70, Efforts[i], "in.png", "out.png"));
        }
    }

    [Fact]
    public void Args_Gif2Webp()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.Palette(8, 8, 4), format: ImageFormat.Gif, frames: 3);
        var a = Adapter("webp.gif2webp");
        foreach (var effort in Efforts)
        {
            // gif2webp would copy the GIF's XMP by default: the policy metadata is written afterwards instead.
            Assert.Equal(["-min_size", "-m", "6", "-mt", "-metadata", "none", "-quiet", "in.gif", "-o", "out.webp"], Args(a, plain, EncodeKind.Lossless, null, effort, "in.gif", "out.webp"));
            Assert.Equal(["-lossy", "-q", "70", "-m", "6", "-metadata", "none", "-quiet", "in.gif", "-o", "out.webp"], Args(a, plain, EncodeKind.Lossy, 70, effort, "in.gif", "out.webp", threads: 1));
        }
    }

    [Fact]
    public void Args_FfmpegFallbacks()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        using var alpha = Source(dir, TestImages.WithAlpha(8, 8));
        string[] head = ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", "in.png"];
        string[] effort = ["5", "7", "9", "9"], cpu = ["8", "6", "3", "1"], level = ["4", "6", "6", "6"];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal([.. head, "-c:v", "libjxl", "-distance", "1.5", "-effort", effort[i], "-threads", "4", "out.jxl"], Args(Adapter("jxl.ffmpeg"), plain, EncodeKind.Lossy, 1.5, Efforts[i], "in.png", "out.jxl"));
            Assert.Equal([.. head, "-c:v", "libjxl", "-distance", "0", "-effort", effort[i], "-threads", "4", "out.jxl"], Args(Adapter("jxl.ffmpeg"), plain, EncodeKind.Lossless, null, Efforts[i], "in.png", "out.jxl"));
            Assert.Equal([.. head, "-c:v", "libaom-av1", "-crf", "30", "-cpu-used", cpu[i], "-still-picture", "1", "-pix_fmt", "yuv420p10le", "-threads", "4", "out.avif"], Args(Adapter("avif.ffmpeg"), plain, EncodeKind.Lossy, 30, Efforts[i], "in.png", "out.avif"));
            Assert.Equal([.. head, "-c:v", "libwebp", "-quality", "75", "-compression_level", level[i], "-pix_fmt", "yuv420p", "-threads", "4", "out.webp"], Args(Adapter("webp.ffmpeg"), plain, EncodeKind.Lossy, 75, Efforts[i], "in.png", "out.webp"));
        }

        Assert.Contains("yuv444p10le", Args(Adapter("avif.ffmpeg"), plain, EncodeKind.Lossy, 20, EffortLevel.Fast, "in.png", "out.avif"));
        Assert.Contains("yuva420p", Args(Adapter("webp.ffmpeg"), alpha, EncodeKind.Lossy, 75, EffortLevel.Fast, "in.png", "out.webp"));
        Assert.Equal([.. head, "-c:v", "libwebp", "-lossless", "1", "-compression_level", "6", "-quality", "100", "-pix_fmt", "bgra", "-threads", "4", "out.webp"], Args(Adapter("webp.ffmpeg"), plain, EncodeKind.Lossless, null, EffortLevel.Fast, "in.png", "out.webp"));
    }

    [Fact]
    public void Scales_And_InitialGuesses_AreMonotone()
    {
        using var dir = new TempDir("args");
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        foreach (string id in new[] { "jxl.cjxl", "avif.avifenc", "webp.cwebp", "jpeg.sjpeg", "png.pngquant", "webp.gif2webp", "jxl.ffmpeg", "avif.ffmpeg", "webp.ffmpeg" })
        {
            var a = Adapter(id);
            double previous = a.InitialGuess(30, plain.Analysis);
            for (int target = 35; target <= 99; target += 5)
            {
                double guess = a.InitialGuess(target, plain.Analysis);
                Assert.InRange(guess, a.Scale.Min, a.Scale.Max);
                Assert.True(a.Scale.HigherIsBetter ? guess >= previous : guess <= previous, $"{id} not monotone at {target}");
                Assert.Equal(guess, a.Scale.Normalize(guess));
                previous = guess;
            }
        }

        Assert.Equal(ParameterScale.None, Adapter("png.oxipng").Scale);
        Assert.Equal(1.23, Adapter("jxl.cjxl").Scale.Normalize(1.2349));
        Assert.Equal(100, Adapter("webp.cwebp").Scale.Normalize(140));
    }

    [Fact]
    public void Supports_FollowsFormatCapabilities()
    {
        using var dir = new TempDir("args");
        using var deep = Source(dir, TestImages.Deep16(8, 8));
        using var alpha = Source(dir, TestImages.WithAlpha(8, 8));
        using var plain = Source(dir, TestImages.PhotoLike(8, 8));
        Assert.True(Adapter("jxl.cjxl").Supports(EncodeKind.JpegTranscode, plain.Analysis, ImageFormat.Jpeg));
        Assert.False(Adapter("jxl.cjxl").Supports(EncodeKind.JpegTranscode, plain.Analysis, ImageFormat.Png));
        Assert.False(Adapter("avif.avifenc").Supports(EncodeKind.Lossless, deep.Analysis, ImageFormat.Png));
        Assert.True(Adapter("avif.avifenc").Supports(EncodeKind.Lossy, deep.Analysis, ImageFormat.Png));
        Assert.False(Adapter("webp.cwebp").Supports(EncodeKind.Lossless, deep.Analysis, ImageFormat.Png));
        Assert.False(Adapter("jpeg.sjpeg").Supports(EncodeKind.Lossy, alpha.Analysis, ImageFormat.Png));
        Assert.True(Adapter("jpeg.sjpeg").Supports(EncodeKind.Optimize, plain.Analysis, ImageFormat.Jpeg));
        Assert.False(Adapter("jpeg.sjpeg").Supports(EncodeKind.Optimize, plain.Analysis, ImageFormat.Png));
        Assert.True(Adapter("png.oxipng").Supports(EncodeKind.Optimize, plain.Analysis, ImageFormat.Apng));
        Assert.False(Adapter("png.ect").Supports(EncodeKind.Optimize, plain.Analysis, ImageFormat.Apng));
        Assert.True(Adapter("webp.gif2webp").Supports(EncodeKind.Lossless, plain.Analysis, ImageFormat.Gif));
        Assert.False(Adapter("webp.gif2webp").Supports(EncodeKind.Lossless, plain.Analysis, ImageFormat.Png));
        Assert.False(Adapter("avif.ffmpeg").Supports(EncodeKind.Lossy, alpha.Analysis, ImageFormat.Png));
        Assert.DoesNotContain(EncodeKind.Lossy, Adapter("png.oxipng").Kinds);
    }

    internal static ImageMetadata FullMetadata() => new()
    {
        Icc = PngCodecTests.SyntheticIcc("RGB "),
        Exif = Decoding.ExifTestData.Build(false, 1, withGps: false),
        Xmp = Decoding.ExifTestData.Xmp,
    };

    private static IReadOnlyList<string> Args(IEncoderAdapter a, PreparedSource s, EncodeKind kind, double? p, EffortLevel effort, string input, string output, int threads = 4, ChromaMode chroma = ChromaMode.Auto) =>
        a.BuildArguments(new EncodeRequest(s, kind, p, effort, chroma, threads, output), input);
}
