using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Carbonado.UI.Effects;
using Carbonado.UI.Tests.TestSupport;

namespace Carbonado.UI.Tests.Effects;

public class EffectsTests
{
    private const int Size = 256;

    [Fact]
    public void Shaders_LoadFromPackUri_AndChangeTheSoftwareRender() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        string dir = Snapshot.ArtifactsDir("ui/effects");
        var plain = Render(null, System.IO.Path.Combine(dir, "plain.png"));
        foreach (var (name, effect) in new (string, Effect)[]
                 {
                     ("aberration", new ChromaticAberrationEffect { Amount = 1, Center = new Point(0, 0) }),
                     ("ripple", new RippleEffect { Radius = 0.35, Amplitude = 0.04, Thickness = 0.1, AspectRatio = 1 }),
                     ("grain", new GrainEffect { Amount = 0.4, Time = 1.5, Size = new Size(Size, Size) }),
                 })
        {
            var bmp = Render(effect, System.IO.Path.Combine(dir, name + ".png"));
            Assert.True(CountDifferentPixels(plain, bmp) > 200, $"{name} did not change the render");
        }
    });

    [Fact]
    public void Aberration_SplitsChannelsAtEdges() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var bmp = Render(new ChromaticAberrationEffect { Amount = 1, Center = new Point(0, 0) }, null);
        // Right edge of the white square (x = 3/4 of the width): red is sampled further out than blue → colored fringe.
        bool fringe = false;
        int edge = Size * 3 / 4;
        for (int x = edge - 8; x < edge + 8 && !fringe; x++)
        {
            var c = Snapshot.PixelAt(bmp, x, Size / 2);
            fringe = Math.Abs(c.R - c.B) > 40;
        }

        Assert.True(fringe, "no chromatic fringe found");
    });

    private static BitmapSource Render(Effect? effect, string? path)
    {
        var grid = new Grid { Effect = effect, Background = Brushes.Black };
        grid.Children.Add(new Rectangle { Fill = Brushes.White, Margin = new Thickness(Size / 4.0) });
        return Snapshot.Render(grid, Size, Size, path);
    }

    private static int CountDifferentPixels(BitmapSource a, BitmapSource b)
    {
        var pa = new byte[Size * Size * 4];
        var pb = new byte[Size * Size * 4];
        a.CopyPixels(pa, Size * 4, 0);
        b.CopyPixels(pb, Size * 4, 0);
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
