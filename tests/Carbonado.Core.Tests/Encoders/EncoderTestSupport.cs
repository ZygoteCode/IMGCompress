using Carbonado.Core.Analysis;
using Carbonado.Core.Encoders;
using Carbonado.Core.Encoders.Adapters;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Encoders;

/// <summary>Builders for encoder tests (a local analysis: the analysis workstream is independent).</summary>
public static class EncoderTestSupport
{
    public static ToolLocator Locator => FormatFixtures.Locator;

    /// <summary>Exact facts the adapters look at; content kind from the colour count only.</summary>
    public static ImageAnalysis Analyze(PixelBuffer px)
    {
        bool opaque = true, binary = true, gray = true, deep = false;
        var colors = new HashSet<ulong>();
        for (int y = 0; y < px.Height; y++)
        {
            for (int x = 0; x < px.Width; x++)
            {
                Rgba64 p = Codecs.PixelAssert.At(px, x, y);
                opaque &= p.A == ushort.MaxValue;
                binary &= p.A is 0 or ushort.MaxValue;
                gray &= p.R == p.G && p.G == p.B;
                deep |= px.Depth == SampleDepth.Sixteen && (p.R % 257 != 0 || p.G % 257 != 0 || p.B % 257 != 0 || p.A % 257 != 0);
                if (colors.Count <= 65536)
                {
                    colors.Add(((ulong)p.R << 48) | ((ulong)p.G << 32) | ((ulong)p.B << 16) | p.A);
                }
            }
        }

        int unique = colors.Count > 65536 ? -1 : colors.Count;
        var alpha = opaque ? AlphaUsage.Opaque : binary ? AlphaUsage.Binary : AlphaUsage.Translucent;
        var kind = unique is >= 0 and <= 4096 ? ContentKind.Graphic : ContentKind.Photo;
        return new ImageAnalysis(px.Width, px.Height, alpha, gray, unique, unique is >= 0 and <= 256, deep, kind, 0, 0);
    }

    /// <summary>A prepared source over a copy of <paramref name="px"/>, with the original written as PNG.</summary>
    public static PreparedSource Source(TempDir dir, PixelBuffer px, ImageMetadata? metadata = null, bool preserveInvisible = false, string? originalPath = null, ImageFormat format = ImageFormat.Png, int frames = 1)
    {
        string original = originalPath ?? dir.File($"original-{Guid.NewGuid():N}.png");
        if (originalPath is null)
        {
            using var fs = File.Create(original);
            Carbonado.Core.Codecs.PngEncoder.EncodeFast(fs, px, metadata);
        }

        var copy = px.Clone();
        return PreparedSource.Create(original, format, copy, metadata ?? new ImageMetadata(), Analyze(copy), dir.Sub("work-" + Guid.NewGuid().ToString("N")[..6]), frames, preserveInvisible);
    }

    public static IEncoderAdapter Adapter(string id, ToolLocator? tools = null, ProcessRunner? runner = null)
    {
        tools ??= Locator;
        runner ??= ProcessRunner.Shared;
        return id switch
        {
            "jxl.cjxl" => new CjxlAdapter(tools, runner),
            "avif.avifenc" => new AvifencAdapter(tools, runner),
            "webp.cwebp" => new CwebpAdapter(tools, runner),
            "jpeg.sjpeg" => new SjpegAdapter(tools, runner),
            "png.oxipng" => new OxipngAdapter(tools, runner),
            "png.ect" => new EctAdapter(tools, runner),
            "png.pngquant" => new PngquantAdapter(tools, runner),
            "webp.gif2webp" => new Gif2WebpAdapter(tools, runner),
            "png.builtin" => new BuiltinPngAdapter(tools, runner),
            "jxl.ffmpeg" => new FfmpegJxlAdapter(tools, runner),
            "avif.ffmpeg" => new FfmpegAvifAdapter(tools, runner),
            "webp.ffmpeg" => new FfmpegWebPAdapter(tools, runner),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
        };
    }

    /// <summary>Skips the current test unless every tool the adapter needs is installed.</summary>
    public static IEncoderAdapter RequireAdapter(string id)
    {
        var adapter = Adapter(id);
        foreach (ToolId tool in adapter.RequiredTools)
        {
            TestTools.Require(tool);
        }

        return adapter;
    }

    public static PixelBuffer Image(string name) => name switch
    {
        "photo" => TestImages.PhotoLike(96, 64),
        "graphic" => TestImages.Graphic(120, 80),
        "alpha" => TestImages.WithAlpha(96, 64),
        "gray" => TestImages.Gray(80, 60),
        "deep" => TestImages.Deep16(64, 48),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
