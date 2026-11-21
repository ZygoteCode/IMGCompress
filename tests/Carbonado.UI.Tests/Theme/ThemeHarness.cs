using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Carbonado.UI.Tests.TestSupport;
using MotionVsm = Carbonado.UI.Theme.MotionVisualStateManager;

namespace Carbonado.UI.Tests.Theme;

/// <summary>
/// Helpers for theme and control snapshots: loads Theme.xaml once per STA thread, stages elements on the themed
/// background, drives visual states deterministically (storyboards settled, not ticking) and renders PNGs.
/// </summary>
internal static class ThemeHarness
{
    public const string ThemePath = "Theme/Theme.xaml";

    [ThreadStatic]
    private static ResourceDictionary? t_theme;

    /// <summary>The theme for the current STA thread.</summary>
    public static ResourceDictionary Theme => t_theme ??= Snapshot.LoadDictionary(ThemePath);

    /// <summary>A grid carrying the theme that centers <paramref name="element"/> (templates resolve on layout).</summary>
    public static Grid Stage(FrameworkElement element, Thickness? margin = null)
    {
        var stage = new Grid();
        stage.Resources.MergedDictionaries.Add(Theme);
        stage.Margin = margin ?? new Thickness(0);
        stage.Children.Add(element);
        return stage;
    }

    /// <summary>Measures and arranges <paramref name="element"/> at the given size (applies templates).</summary>
    public static void Layout(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Moves <paramref name="control"/> to <paramref name="state"/> without transitions and settles it.</summary>
    public static void GoTo(Control control, string state, TimeSpan? at = null)
    {
        Assert.True(VisualStateManager.GoToState(control, state, false), $"{control.GetType().Name} has no visual state '{state}'");
        Settle(control, at);
    }

    /// <summary>
    /// Freezes every running state storyboard of <paramref name="control"/>: at <paramref name="at"/> when given,
    /// otherwise at its final frame (looping storyboards: first frame).
    /// </summary>
    public static void Settle(Control control, TimeSpan? at = null)
    {
        if (VisualTreeHelper.GetChildrenCount(control) == 0 || VisualTreeHelper.GetChild(control, 0) is not FrameworkElement root)
        {
            return;
        }

        foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(root))
        {
            if (group.CurrentState?.Storyboard is not { } storyboard)
            {
                continue;
            }

            bool infinite = MotionVsm.IsInfinite(storyboard);
            var time = at ?? (infinite ? TimeSpan.Zero : TimeSpan.FromSeconds(30));
            storyboard.SeekAlignedToLastTick(root, time, TimeSeekOrigin.BeginTime);
            storyboard.Pause(root);
        }
    }

    /// <summary>Renders <paramref name="stage"/> on the themed background to tests/artifacts/ui/&lt;suite&gt;/&lt;name&gt;.png.</summary>
    public static BitmapSource Render(FrameworkElement stage, int width, int height, string suite, string name) =>
        Snapshot.Render(stage, width, height, Path.Combine(Snapshot.ArtifactsDir(suite), name + ".png"), Theme);

    /// <summary>
    /// Renders a root-only control (ToolTip, ContextMenu cannot have a parent) over the themed background; saves the
    /// PNG under tests/artifacts/&lt;suite&gt; when <paramref name="suite"/> is set.
    /// </summary>
    public static BitmapSource RenderRoot(Control root, int width, int height, string? suite, string name)
    {
        root.Resources.MergedDictionaries.Add(Theme);
        if (Theme.Contains(root.GetType()))
        {
            root.Style = (Style)Theme[root.GetType()];
        }

        Layout(root, width, height);
        Sta.DoEvents();
        root.UpdateLayout();

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle((Brush)Theme["B.Window"], null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(root);
        bitmap.Freeze();
        if (suite is not null)
        {
            Save(bitmap, Path.Combine(Snapshot.ArtifactsDir(suite), name + ".png"));
        }

        return bitmap;
    }

    /// <summary>Writes a PNG.</summary>
    public static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Adds the element's keyboard focus visual (normally an adorner) on top of it, as the user sees it.</summary>
    public static void ShowFocusVisual(Grid stage, FrameworkElement element)
    {
        if (element.FocusVisualStyle is not { } style)
        {
            return;
        }

        var origin = element.TranslatePoint(new Point(0, 0), stage);
        stage.Children.Add(new Control
        {
            Style = style,
            Width = element.ActualWidth,
            Height = element.ActualHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(origin.X, origin.Y, 0, 0),
            IsHitTestVisible = false,
        });
    }

    /// <summary>
    /// Pixels whose color differs between <paramref name="a"/> and <paramref name="b"/> by more than
    /// <paramref name="tolerance"/> (sum of the BGR channel differences), optionally only inside <paramref name="region"/>.
    /// </summary>
    public static int CountDifferent(BitmapSource a, BitmapSource b, Rect? region = null, int tolerance = 12)
    {
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        int width = a.PixelWidth, height = a.PixelHeight;
        var pa = new byte[width * height * 4];
        var pb = new byte[width * height * 4];
        a.CopyPixels(pa, width * 4, 0);
        b.CopyPixels(pb, width * 4, 0);
        var area = region ?? new Rect(0, 0, width, height);
        int x0 = Math.Max(0, (int)Math.Floor(area.Left)), y0 = Math.Max(0, (int)Math.Floor(area.Top));
        int x1 = Math.Min(width, (int)Math.Ceiling(area.Right)), y1 = Math.Min(height, (int)Math.Ceiling(area.Bottom));
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int o = ((y * width) + x) * 4;
                int delta = Math.Abs(pa[o] - pb[o]) + Math.Abs(pa[o + 1] - pb[o + 1]) + Math.Abs(pa[o + 2] - pb[o + 2]);
                count += delta > tolerance ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Differing pixels in the <paramref name="band"/>-wide frame just outside <paramref name="bounds"/>.</summary>
    public static int CountDifferentOutside(BitmapSource a, BitmapSource b, Rect bounds, double band)
    {
        var outer = bounds;
        outer.Inflate(band, band);
        return CountDifferent(a, b, outer) - CountDifferent(a, b, bounds);
    }

    /// <summary>
    /// Alpha the element paints at <paramref name="point"/> when rendered alone on a transparent surface, i.e. whether
    /// its chrome has a background there (independent of the color matching the window).
    /// </summary>
    public static byte AlphaAt(FrameworkElement stage, int width, int height, Point point)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(stage);
        return Snapshot.PixelAt(bitmap, (int)point.X, (int)point.Y).A;
    }
}
