using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Carbonado.UI.Tests.TestSupport;

/// <summary>Renders WPF elements to PNG files for visual review and pixel assertions.</summary>
public static class Snapshot
{
    static Snapshot() => EnsurePackScheme();

    /// <summary>
    /// Registers the pack:// URI parser and web-request factory (normally done by Application's static constructor)
    /// so pack://application URIs resolve in tests without creating an Application instance.
    /// </summary>
    public static void EnsurePackScheme()
    {
        _ = PackUriHelper.UriSchemePack;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    }

    /// <summary>tests/artifacts/&lt;suite&gt; under the repository root (created on demand).</summary>
    public static string ArtifactsDir(string suite)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Carbonado.sln")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? Path.GetTempPath();
        string path = Path.Combine(root, "tests", "artifacts", suite);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Loads a resource dictionary from Carbonado.UI by relative path (e.g. "Theme/Tokens.xaml").</summary>
    public static ResourceDictionary LoadDictionary(string relativePath) =>
        new() { Source = new Uri($"pack://application:,,,/Carbonado.UI;component/{relativePath}", UriKind.Absolute) };

    /// <summary>
    /// Lays out <paramref name="element"/> at the given size on a themed window-colored background, renders it at
    /// 96 DPI and writes a PNG when <paramref name="path"/> is not null. Returns the frozen bitmap.
    /// Must run on an STA thread (see <see cref="Sta"/>).
    /// </summary>
    public static BitmapSource Render(FrameworkElement element, int width, int height, string? path = null, ResourceDictionary? theme = null)
    {
        var host = new Border
        {
            Width = width,
            Height = height,
            Child = element,
        };
        if (theme is not null)
        {
            host.Resources.MergedDictionaries.Add(theme);
            host.SetResourceReference(Border.BackgroundProperty, "B.Window");
        }
        else
        {
            host.Background = Brushes.Black;
        }

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        Sta.DoEvents();
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        host.Child = null;
        return rtb;
    }

    /// <summary>Reads one pixel (premultiplied BGRA) from a rendered bitmap.</summary>
    public static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }

    /// <summary>Number of distinct colors in the bitmap (sampled on a grid) — guards against blank renders.</summary>
    public static int DistinctColors(BitmapSource bitmap, int step = 4)
    {
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var bytes = new byte[w * h * 4];
        bitmap.CopyPixels(bytes, w * 4, 0);
        var set = new HashSet<int>();
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                int o = ((y * w) + x) * 4;
                set.Add(BitConverter.ToInt32(bytes, o));
            }
        }

        return set.Count;
    }
}
