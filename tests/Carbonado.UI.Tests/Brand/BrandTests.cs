using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.UI.Brand;
using Carbonado.UI.Tests.TestSupport;

namespace Carbonado.UI.Tests.Brand;

public class BrandTests
{
    private static readonly int[] s_iconSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void DiamondMark_RendersAtEveryIconSize() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        string dir = Snapshot.ArtifactsDir("ui/brand");
        var theme = Snapshot.LoadDictionary("Theme/Tokens.xaml");
        foreach (int size in s_iconSizes)
        {
            var bmp = Snapshot.Render(new DiamondMark(), size, size, Path.Combine(dir, $"mark-{size}.png"), theme);
            Assert.True(Snapshot.DistinctColors(bmp, 1) >= Math.Min(24, size), $"{size}px mark is nearly flat");

            // Centered and inside its box: the corners keep the window color.
            Color background = Snapshot.PixelAt(bmp, 0, 0);
            Assert.Equal(background, Snapshot.PixelAt(bmp, size - 1, size - 1));
            Assert.NotEqual(background, Snapshot.PixelAt(bmp, size / 2, size / 2));
        }

        // Review sheet: every size side by side plus 8x pixel views of the small ones.
        var sheet = new WrapPanel { Margin = new Thickness(16) };
        foreach (int size in s_iconSizes)
        {
            sheet.Children.Add(new DiamondMark { Width = size, Height = size, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center });
        }

        foreach (int size in new[] { 16, 24, 32 })
        {
            var pixels = Snapshot.Render(new DiamondMark(), size, size, null, theme);
            var zoomed = new Image { Source = pixels, Width = size * 8, Height = size * 8, Margin = new Thickness(12) };
            RenderOptions.SetBitmapScalingMode(zoomed, BitmapScalingMode.NearestNeighbor);
            sheet.Children.Add(zoomed);
        }

        sheet.Children.Add(new DiamondMark { Width = 256, Height = 256, Glint = 1, Margin = new Thickness(12) });
        Snapshot.Render(sheet, 1100, 820, Path.Combine(dir, "mark-sheet.png"), theme);
    });

    [Fact]
    public void DiamondMark_GlintAddsAFlareAndIsClamped() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var theme = Snapshot.LoadDictionary("Theme/Tokens.xaml");
        var rest = Snapshot.Render(new DiamondMark(), 128, 128, null, theme);
        var lit = Snapshot.Render(new DiamondMark { Glint = 1 }, 128, 128, null, theme);
        Assert.True(CountDifferent(rest, lit) > 400, "glint did not change the mark");

        var mark = new DiamondMark { Glint = 4 };
        Assert.Equal(1, mark.Glint);
        mark.Glint = -2;
        Assert.Equal(0, mark.Glint);
    });

    [Fact]
    public void Wordmark_LettersAreFacetedGeometrySetSideBySide() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        IReadOnlyList<Geometry> letters = WordmarkGeometry.Letters;
        double previousRight = double.NegativeInfinity;
        for (int i = 0; i < letters.Count; i++)
        {
            Assert.True(letters[i].IsFrozen, $"letter {i} is not frozen");
            Rect bounds = letters[i].Bounds;
            Assert.Equal(0, bounds.Top, 1e-6);
            Assert.Equal(WordmarkGeometry.CapHeight, bounds.Bottom, 1e-6);
            Assert.Equal(WordmarkGeometry.Advances[i], bounds.Width, 1e-6);
            Assert.True(letters[i].GetArea() > 0.2 * bounds.Width * bounds.Height, $"letter {i} is too thin");
            Assert.True(letters[i].GetArea() < 0.7 * bounds.Width * bounds.Height, $"letter {i} has no counters");

            double left = WordmarkGeometry.Offset(i);
            Assert.True(left >= previousRight + (0.17 * WordmarkGeometry.CapHeight), $"letter {i} touches its neighbour");
            previousRight = left + bounds.Width;
        }

        Geometry word = WordmarkGeometry.Compose();
        Assert.Equal(WordmarkGeometry.Width(), word.Bounds.Width, 1e-6);

        var path = new System.Windows.Shapes.Path { Data = word, Fill = (Brush)Snapshot.LoadDictionary("Theme/Tokens.xaml")["B.Diamond50"], Stretch = Stretch.Uniform, Margin = new Thickness(48) };
        var bmp = Snapshot.Render(path, 1100, 220, Path.Combine(Snapshot.ArtifactsDir("ui/brand"), "wordmark.png"), Snapshot.LoadDictionary("Theme/Tokens.xaml"));
        Assert.True(Snapshot.DistinctColors(bmp) > 4);
    });

    private static int CountDifferent(BitmapSource a, BitmapSource b)
    {
        int w = a.PixelWidth, h = a.PixelHeight;
        var pa = new byte[w * h * 4];
        var pb = new byte[w * h * 4];
        a.CopyPixels(pa, w * 4, 0);
        b.CopyPixels(pb, w * 4, 0);
        int diff = 0;
        for (int i = 0; i < pa.Length; i += 4)
        {
            if (pa[i] != pb[i] || pa[i + 1] != pb[i + 1] || pa[i + 2] != pb[i + 2])
            {
                diff++;
            }
        }

        return diff;
    }
}
