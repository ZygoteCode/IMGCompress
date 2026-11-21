using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.TestSupport;

/// <summary>
/// Deterministic synthetic test images plus helpers to save/load them with WIC (independent of Carbonado's own codecs).
/// </summary>
public static class TestImages
{
    /// <summary>Smooth RGB gradient (photo-like smoothness, no noise).</summary>
    public static PixelBuffer Gradient(int width = 256, int height = 192)
    {
        var px = PixelBuffer.Create(width, height, SampleDepth.Eight);
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba32((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)((x + y) * 255 / Math.Max(1, width + height - 2)));
            }
        }

        return px;
    }

    /// <summary>Photo-like content: layered sines, soft shapes, fine texture and mild noise (seeded).</summary>
    public static PixelBuffer PhotoLike(int width = 320, int height = 240, int seed = 1)
    {
        var rng = new Random(seed);
        var px = PixelBuffer.Create(width, height, SampleDepth.Eight);
        double p1 = rng.NextDouble() * Math.PI, p2 = rng.NextDouble() * Math.PI, p3 = rng.NextDouble() * Math.PI;
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                double u = x / (double)width, v = y / (double)height;
                double sky = 0.5 + (0.5 * Math.Sin((u * 3.1) + p1) * Math.Cos((v * 2.3) + p2));
                double hills = v > 0.55 + (0.12 * Math.Sin((u * 9) + p3)) ? 1 : 0;
                double texture = 0.08 * Math.Sin(x * 0.9) * Math.Sin(y * 1.3);
                double noise = (rng.NextDouble() - 0.5) * 0.06;
                double r = (sky * 0.6) + (hills * 0.15) + texture + noise;
                double g = (sky * 0.7) + (hills * 0.35) + texture + noise;
                double b = (sky * 0.95) - (hills * 0.3) + texture + noise;
                row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b));
            }
        }

        return px;
    }

    /// <summary>Synthetic UI-like graphic: flat panels, borders, stripes and glyph-like strokes, few colors.</summary>
    public static PixelBuffer Graphic(int width = 320, int height = 200)
    {
        var px = PixelBuffer.Create(width, height, SampleDepth.Eight);
        var palette = new[]
        {
            new Rgba32(18, 20, 30), new Rgba32(40, 44, 64), new Rgba32(56, 225, 255), new Rgba32(139, 123, 255),
            new Rgba32(244, 242, 255), new Rgba32(255, 95, 168), new Rgba32(43, 240, 161),
        };
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                Rgba32 c = palette[0];
                if (y < 24) c = palette[1];
                if (x > 16 && x < 120 && y > 40 && y < 180) c = palette[1];
                if ((x == 16 || x == 120) && y > 40 && y < 180) c = palette[2];
                if (x > 140 && x < 300 && y > 40 && y < 70) c = ((x / 8) % 2 == 0) ? palette[3] : palette[1];
                if (x > 140 && y > 90 && y < 180 && ((x + y) % 13 < 2)) c = palette[4];
                if (y > 100 && y < 104 && x > 24 && x < 110) c = palette[5];
                if (Math.Abs(x - 200) + Math.Abs(y - 140) < 18) c = palette[6];
                row[x] = c;
            }
        }

        return px;
    }

    /// <summary>Photo-like content with an alpha gradient, a fully transparent band and a binary-alpha disc.</summary>
    public static PixelBuffer WithAlpha(int width = 256, int height = 192)
    {
        var px = PhotoLike(width, height, seed: 7);
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                byte a = (byte)(x * 255 / Math.Max(1, width - 1));
                if (y < height / 6) a = 0;
                double dx = x - (width * 0.7), dy = y - (height * 0.6);
                if ((dx * dx) + (dy * dy) < (height * height / 25.0)) a = 255;
                row[x].A = a;
            }
        }

        return px;
    }

    /// <summary>Image using exactly <paramref name="colors"/> distinct opaque colors (≤ 256 → palette-friendly).</summary>
    public static PixelBuffer Palette(int width = 128, int height = 96, int colors = 16)
    {
        var px = PixelBuffer.Create(width, height, SampleDepth.Eight);
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                int i = ((x / 8) + ((y / 8) * 7)) % colors;
                row[x] = new Rgba32((byte)(i * 37 % 256), (byte)(i * 91 % 256), (byte)(i * 151 % 256));
            }
        }

        return px;
    }

    /// <summary>Grayscale (R = G = B) gradient with texture.</summary>
    public static PixelBuffer Gray(int width = 160, int height = 120)
    {
        var px = PixelBuffer.Create(width, height, SampleDepth.Eight);
        for (int y = 0; y < height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < width; x++)
            {
                byte v = ToByte((x / (double)width * 0.8) + (0.1 * Math.Sin(y * 0.5)));
                row[x] = new Rgba32(v, v, v);
            }
        }

        return px;
    }

    /// <summary>16-bit gradient whose low bits matter (cannot be reduced to 8 bits exactly).</summary>
    public static PixelBuffer Deep16(int width = 128, int height = 96)
    {
        var px = PixelBuffer.Create(width, height, SampleDepth.Sixteen);
        for (int y = 0; y < height; y++)
        {
            var row = px.Row64(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba64((ushort)(x * 511 + 3), (ushort)(y * 677 + 5), (ushort)((x * y) % 65521));
            }
        }

        return px;
    }

    /// <summary>JPEG photos shipped with Windows (realistic camera content). Empty when unavailable.</summary>
    public static IReadOnlyList<string> SystemPhotos(int max = 6)
    {
        string root = @"C:\Windows\Web";
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(f => f.Length > 200_000)
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Saves a buffer as PNG with WPF's encoder (8-bit → Bgra32 or Bgr24 when opaque; 16-bit → Rgba64).</summary>
    public static void SavePngWithWic(PixelBuffer px, string path)
    {
        var source = ToBitmapSource(px);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Saves a buffer as baseline JPEG (quality 1..100) with WPF's encoder.</summary>
    public static void SaveJpegWithWic(PixelBuffer px, string path, int quality = 90)
    {
        using var eight = px.Depth == SampleDepth.Eight ? px.Clone() : px.ToDepth(SampleDepth.Eight);
        var src = new FormatConvertedBitmap(ToBitmapSource(eight), PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Decodes any WIC-supported file to an 8-bit RGBA buffer (reference decoder for tests).</summary>
    public static PixelBuffer LoadWithWic(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
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

    /// <summary>Converts a buffer to a frozen WPF bitmap (non-premultiplied Bgra32, or Rgba64 for 16-bit).</summary>
    public static BitmapSource ToBitmapSource(PixelBuffer px)
    {
        BitmapSource bmp;
        if (px.Depth == SampleDepth.Eight)
        {
            var bytes = new byte[px.Width * px.Height * 4];
            for (int y = 0; y < px.Height; y++)
            {
                var row = px.Row32(y);
                int o = y * px.Width * 4;
                for (int x = 0; x < px.Width; x++, o += 4)
                {
                    bytes[o] = row[x].B;
                    bytes[o + 1] = row[x].G;
                    bytes[o + 2] = row[x].R;
                    bytes[o + 3] = row[x].A;
                }
            }

            bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Bgra32, null, bytes, px.Width * 4);
        }
        else
        {
            var bytes = new byte[px.Width * px.Height * 8];
            for (int y = 0; y < px.Height; y++)
            {
                px.RowBytes(y).CopyTo(bytes.AsSpan(y * px.Width * 8, px.Width * 8));
            }

            bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Rgba64, null, bytes, px.Width * 8);
        }

        bmp.Freeze();
        return bmp;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
