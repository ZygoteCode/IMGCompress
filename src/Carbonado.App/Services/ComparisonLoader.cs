using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.App.Pages.Compare;
using Carbonado.Core.Imaging;
using Carbonado.Core.Metrics;

namespace Carbonado.App.Services;

/// <summary>
/// Decodes an original/output pair for the Compare page and builds the SSIMULACRA2 error heat-map
/// (dimmed original luminance with the per-pixel error painted over it in the brand spectrum).
/// </summary>
public sealed class ComparisonLoader(AppServices services) : IComparisonLoader
{
    public async Task<ComparisonImages> LoadAsync(string original, string output, CancellationToken ct)
    {
        using var before = await DecodeAsync(original, ct).ConfigureAwait(false);
        using var after = await DecodeAsync(output, ct).ConfigureAwait(false);
        return new ComparisonImages(ToBitmap(before), ToBitmap(after), before.Width, before.Height);
    }

    public async Task<(ImageSource Map, double Score)> HeatmapAsync(string original, string output, CancellationToken ct)
    {
        using var before = await DecodeAsync(original, ct).ConfigureAwait(false);
        using var after = await DecodeAsync(output, ct).ConfigureAwait(false);
        if (before.Width != after.Width || before.Height != after.Height)
        {
            throw new InvalidDataException($"Size mismatch: {before.Width}×{before.Height} vs {after.Width}×{after.Height}.");
        }

        var stops = BrandStops();
        return await Task.Run(() =>
        {
            using var reference = new Ssimulacra2Reference(before);
            var map = reference.ScoreWithMap(after, ct);
            return ((ImageSource)Colorize(before, map, stops), map.Score);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Decodes any supported file to 8-bit RGBA (engine loader when available, WIC otherwise).</summary>
    private Task<PixelBuffer> DecodeAsync(string path, CancellationToken ct) =>
        services.Tools.Loader is { } loader
            ? DecodeWithEngineAsync(loader, path, ct)
            : Task.Run(() => WicPixels.Load(path), ct);

    private static async Task<PixelBuffer> DecodeWithEngineAsync(Core.Decoding.IImageLoader loader, string path, CancellationToken ct)
    {
        using var decoded = await loader.LoadAsync(path, new Core.Decoding.LoadOptions(), ct).ConfigureAwait(false);
        var pixels = decoded.DetachPixels();
        if (pixels.Depth == SampleDepth.Eight)
        {
            return pixels;
        }

        using (pixels)
        {
            return pixels.ToDepth(SampleDepth.Eight);
        }
    }

    internal static BitmapSource ToBitmap(PixelBuffer px)
    {
        var eight = px.Depth == SampleDepth.Eight ? px : px.ToDepth(SampleDepth.Eight);
        try
        {
            var bytes = new byte[eight.Width * eight.Height * 4];
            for (int y = 0; y < eight.Height; y++)
            {
                var row = eight.Row32(y);
                int o = y * eight.Width * 4;
                for (int x = 0; x < row.Length; x++, o += 4)
                {
                    bytes[o] = row[x].B;
                    bytes[o + 1] = row[x].G;
                    bytes[o + 2] = row[x].R;
                    bytes[o + 3] = row[x].A;
                }
            }

            var bmp = BitmapSource.Create(eight.Width, eight.Height, 96, 96, PixelFormats.Bgra32, null, bytes, eight.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (!ReferenceEquals(eight, px))
            {
                eight.Dispose();
            }
        }
    }

    /// <summary>The heat-map gradient from the theme tokens: Iris → Rose → Ember → Ice.</summary>
    internal static IReadOnlyList<Color> BrandStops() =>
        new[] { "C.Iris", "C.Rose", "C.Ember", "C.Ice" }.Select(k => System.Windows.Application.Current?.TryFindResource(k) is Color c ? c : Colors.White).ToList();

    /// <summary>Paints the per-pixel error over 30 % of the original luminance, along <paramref name="gradient"/>.</summary>
    internal static BitmapSource Colorize(PixelBuffer original, Ssimulacra2Map map, IReadOnlyList<Color> gradient)
    {
        int w = map.Width, h = map.Height;
        var bytes = new byte[w * h * 4];
        var stops = gradient.Select(c => (R: (float)c.R, G: (float)c.G, B: (float)c.B)).ToArray();
        for (int y = 0; y < h; y++)
        {
            var row = original.Width == w && original.Height == h ? original.Row32(y) : default;
            for (int x = 0; x < w; x++)
            {
                float lum = row.IsEmpty ? 0 : ((0.2126f * row[x].R) + (0.7152f * row[x].G) + (0.0722f * row[x].B)) * 0.30f;
                float e = Math.Clamp(map.Error[(y * w) + x] * 2.5f, 0f, 1f);
                float t = e * (stops.Length - 1);
                int i = Math.Min((int)t, stops.Length - 2);
                float f = t - i;
                float r = stops[i].R + ((stops[i + 1].R - stops[i].R) * f);
                float g = stops[i].G + ((stops[i + 1].G - stops[i].G) * f);
                float b = stops[i].B + ((stops[i + 1].B - stops[i].B) * f);
                float a = MathF.Sqrt(e);
                int o = ((y * w) + x) * 4;
                bytes[o] = (byte)Math.Clamp((lum * (1 - a)) + (b * a), 0, 255);
                bytes[o + 1] = (byte)Math.Clamp((lum * (1 - a)) + (g * a), 0, 255);
                bytes[o + 2] = (byte)Math.Clamp((lum * (1 - a)) + (r * a), 0, 255);
                bytes[o + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>WIC decoding to an 8-bit RGBA buffer (used before the engine's loader is available).</summary>
internal static class WicPixels
{
    public static PixelBuffer Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var bytes = new byte[w * h * 4];
        bgra.CopyPixels(bytes, w * 4, 0);
        var px = PixelBuffer.Create(w, h, SampleDepth.Eight, clear: false);
        for (int y = 0; y < h; y++)
        {
            var row = px.Row32(y);
            int o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                row[x] = new Rgba32(bytes[o + 2], bytes[o + 1], bytes[o], bytes[o + 3]);
            }
        }

        return px;
    }
}
