using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.UI.Controls;
using Carbonado.UI.Tests.Theme;

namespace Carbonado.UI.Tests.Controls;

/// <summary>A composition of brand controls to render, plus what to do once it is laid out (visual states).</summary>
internal sealed record BrandScene(Func<FrameworkElement> Build, int Width, int Height, Action<FrameworkElement>? AfterLayout = null);

/// <summary>Sample compositions for every brand control in its interesting states (rendered by ControlSnapshotTests).</summary>
internal static class BrandScenes
{
    public static readonly IReadOnlyDictionary<string, BrandScene> All = new Dictionary<string, BrandScene>
    {
        ["PrismButton.Normal"] = new(() => Prism(), 260, 90),
        ["PrismButton.Hover"] = new(() => Prism(), 260, 90, e => ThemeHarness.GoTo(Find<PrismButton>(e), "MouseOver", TimeSpan.FromMilliseconds(230))),
        ["PrismButton.Pressed"] = new(() => Prism(), 260, 90, Press),
        ["PrismButton.Busy"] = new(() => Prism(busy: true), 260, 90),
        ["PrismButton.Disabled"] = new(() => Prism(enabled: false), 260, 90, e => ThemeHarness.GoTo(Find<PrismButton>(e), "Disabled")),
        ["PrismButton.Focused"] = new(() => Prism(), 260, 90, e => ThemeHarness.ShowFocusVisual((Grid)e.Parent, Find<PrismButton>(e))),
        ["GemProgressRing.States"] = new(Rings, 420, 130),
        ["CountUpText.Formats"] = new(() => Counters(frozen: false), 420, 230),
        ["CountUpText.Rolling"] = new(() => Counters(frozen: true), 420, 230),
        ["RadialGauge.Values"] = new(Gauges, 600, 210),
        ["BeforeAfterViewer.Split"] = new(() => Viewer(CompareMode.Split), 480, 300),
        ["BeforeAfterViewer.SideBySide"] = new(() => Viewer(CompareMode.SideBySide), 480, 300),
        ["BeforeAfterViewer.Flicker"] = new(() => Viewer(CompareMode.Flicker), 480, 300),
        ["BeforeAfterViewer.Heatmap"] = new(() => Viewer(CompareMode.Heatmap), 480, 300),
        ["BeforeAfterViewer.Zoomed"] = new(() => Viewer(CompareMode.Split), 480, 300, e => Find<BeforeAfterViewer>(e).ZoomAt(new Point(250, 150), 12)),
        ["DropZone.Idle"] = new(() => Drop(particles: false), 520, 260),
        ["DropZone.DragOver"] = new(() => Drop(particles: true), 520, 260,
            e => ThemeHarness.GoTo(Find<DropZone>(e), "DragOver", TimeSpan.FromMilliseconds(250))),
        ["ParticleField.Dust"] = new(Dust, 480, 240),
        ["NavRail.Selected"] = new(Rail, 120, 440, e => ThemeHarness.GoTo(Find<NavRail>(e).Items.OfType<NavItem>().ElementAt(2), "MouseOver")),
        ["StatCard.Row"] = new(Stats, 760, 250),
        ["BarChart.Fortnight"] = new(Bars, 420, 180),
        ["DonutChart.Formats"] = new(Donut, 220, 220),
        ["Toast.Kinds"] = new(Toasts, 420, 380),
        ["ModalHost.Open"] = new(Modal, 640, 400),
        ["Chip.States"] = new(Chips, 560, 80, e => ThemeHarness.GoTo(Find<Chip>(e, 1), "MouseOver")),
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string name in All.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    public static T Find<T>(DependencyObject root, int index = 0)
        where T : DependencyObject => Descendants(root).OfType<T>().ElementAt(index);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i)))
            {
                yield return child;
            }
        }
    }

    private static Brush Token(string key) => (Brush)ThemeHarness.Theme[key];

    private static Geometry Icon(string key) => (Geometry)ThemeHarness.Theme[key];

    private static FrameworkElement Prism(bool busy = false, bool enabled = true) =>
        new PrismButton { Content = busy ? "Forging…" : "Start forging", Icon = Icon("Icon.Play"), IsBusy = busy, IsEnabled = enabled };

    private static void Press(FrameworkElement root)
    {
        var button = Find<PrismButton>(root);
        ThemeHarness.GoTo(button, "Pressed");
        button.StartRipple(new Point(button.ActualWidth * 0.3, button.ActualHeight * 0.6));
        // Freeze the bloom mid-flight (animations do not tick in a snapshot).
        var ripple = (FrameworkElement)button.Template.FindName("PART_Ripple", button);
        ripple.BeginAnimation(UIElement.OpacityProperty, null);
        ripple.Opacity = 0.45;
        var scale = (ScaleTransform)ripple.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 0.55;
    }

    private static FrameworkElement Rings()
    {
        var grid = new UniformGrid { Columns = 7, Rows = 2, Width = 392 };
        foreach (double size in new[] { 40.0, 28.0 })
        {
            foreach (var (state, value, spin) in new[]
                     {
                         (GemState.Idle, 0.0, false), (GemState.Working, 0.35, false), (GemState.Working, 0.8, false), (GemState.Working, 0, true),
                         (GemState.Done, 1, false), (GemState.Kept, 1, false), (GemState.Failed, 0.4, false),
                     })
            {
                grid.Children.Add(new GemProgressRing { State = state, Value = value, IsIndeterminate = spin, Width = size, Height = size, Thickness = size / 14 });
            }
        }

        return grid;
    }

    private static FrameworkElement Counters(bool frozen)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (format, from, to) in new[]
                 {
                     (CountFormat.Integer, 1240.0, 12840.0), (CountFormat.Bytes, 3.1e9, 3.42 * 1024 * 1024 * 1024),
                     (CountFormat.Percent, 0.5, 0.912), (CountFormat.Score, 79.0, 87.4),
                 })
        {
            var counter = new CountUpText { Format = format, Value = to, FontSize = 34, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) };
            if (frozen)
            {
                counter.FreezeFrame(from, 0.42);
            }

            stack.Children.Add(counter);
        }

        return stack;
    }

    private static FrameworkElement Gauges()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, text) in new[] { (0.91, "−91 %"), (0.42, "−42 %"), (0.0, "0 %") })
        {
            row.Children.Add(new RadialGauge { Value = value, ValueText = text, Caption = "SAVED", Margin = new Thickness(10, 0, 10, 0) });
        }

        return row;
    }

    private static FrameworkElement Viewer(CompareMode mode) => new BeforeAfterViewer
    {
        Mode = mode,
        Before = Photo(posterize: false),
        After = Photo(posterize: true),
        Heatmap = Difference(),
        BeforeLabel = "BEFORE",
        AfterLabel = "AFTER",
        Split = 0.46,
    };

    private static FrameworkElement Drop(bool particles)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new IconPresenter { Icon = Icon("Icon.Import"), Width = 40, Height = 40, StrokeThickness = 1.4 };
        icon.SetResourceReference(IconPresenter.ForegroundProperty, "B.Ice");
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = "Drop images or folders", Style = (Style)ThemeHarness.Theme["Text.Subtitle"], Margin = new Thickness(0, 14, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "PNG · JPEG · WebP · AVIF · JXL · HEIC", Style = (Style)ThemeHarness.Theme["Text.Caption"], HorizontalAlignment = HorizontalAlignment.Center });
        var zone = new DropZone { Content = content, Width = 480, Height = 220 };
        if (!particles)
        {
            return zone;
        }

        var field = new ParticleField { Count = 120, Intensity = 1, Attractor = new Point(0.5, 0.45), IsRunning = false };
        field.Advance(TimeSpan.FromSeconds(1.2));
        var layered = new Grid { Width = 480, Height = 220 };
        layered.Children.Add(field);
        layered.Children.Add(zone);
        return layered;
    }

    private static FrameworkElement Dust()
    {
        var field = new ParticleField { Count = 160, Intensity = 0.9, IsRunning = false };
        field.Advance(TimeSpan.FromSeconds(2));
        return new Grid { Width = 480, Height = 240, Children = { field } };
    }

    private static FrameworkElement Rail()
    {
        var rail = new NavRail { Height = 420 };
        foreach (var (label, icon) in new[]
                 {
                     ("Forge", "Icon.Forge"), ("Compare", "Icon.Compare"), ("Profiles", "Icon.Profiles"),
                     ("Archive", "Icon.Archive"), ("Engines", "Icon.Engines"), ("Settings", "Icon.Settings"),
                 })
        {
            rail.Items.Add(new NavItem { Label = label, Icon = Icon(icon), Shortcut = "Ctrl+1" });
        }

        rail.SelectedIndex = 0;
        return rail;
    }

    private static FrameworkElement Stats()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new StatCard
        {
            Title = "BYTES SAVED", Icon = Icon("Icon.Compress"), Caption = "across 1 284 files", Width = 230, Margin = new Thickness(8),
            Value = new CountUpText { Format = CountFormat.Bytes, Value = 3.42 * 1024 * 1024 * 1024, HorizontalAlignment = HorizontalAlignment.Left },
        });
        row.Children.Add(new StatCard
        {
            Title = "AVERAGE QUALITY", Icon = Icon("Icon.Diamond"), Caption = "SSIMULACRA2 · grade VS", Width = 230, Margin = new Thickness(8),
            Value = "86.9",
        });
        row.Children.Add(new StatCard
        {
            Title = "THIS WEEK", Icon = Icon("Icon.Chart"), Width = 230, Margin = new Thickness(8), Value = "−78 %",
            Content = new BarChart { Height = 44, Values = [3, 5, 4, 8, 6, 9, 7] },
        });
        return row;
    }

    private static FrameworkElement Bars() => new BarChart
    {
        Width = 380,
        Height = 150,
        Values = [12, 18, 9, 24, 31, 27, 15, 22, 35, 29, 41, 38, 26, 44],
        Labels = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14"],
    };

    private static FrameworkElement Donut() => new DonutChart
    {
        Width = 180,
        Height = 180,
        Slices =
        [
            new DonutSlice("JXL", 52, Token("B.Aqua")), new DonutSlice("AVIF", 27, Token("B.Iris")),
            new DonutSlice("WebP", 14, Token("B.Rose")), new DonutSlice("PNG", 7, Token("B.Ember")),
        ],
    };

    private static FrameworkElement Toasts()
    {
        var stack = new StackPanel { Width = 360 };
        foreach (var (kind, title, message) in new[]
                 {
                     (ToastKind.Success, "Batch complete", "1 284 files · 3.42 GB saved · all verified"),
                     (ToastKind.Info, "Engines ready", "8 encoders verified by SHA-256"),
                     (ToastKind.Warning, "Original kept", "aurora.png was already optimal"),
                     (ToastKind.Error, "Could not read file", "scan-0042.tif is truncated"),
                 })
        {
            stack.Children.Add(new Toast { Kind = kind, Title = title, Message = message });
        }

        return stack;
    }

    private static FrameworkElement Modal()
    {
        var page = new Border { Style = (Style)ThemeHarness.Theme["Border.Card"], Margin = new Thickness(24) };
        page.Child = new TextBlock { Text = "Forge", Style = (Style)ThemeHarness.Theme["Text.Display"] };
        var dialog = new StackPanel();
        dialog.Children.Add(new TextBlock { Text = "Replace originals?", Style = (Style)ThemeHarness.Theme["Text.Title"] });
        dialog.Children.Add(new TextBlock
        {
            Text = "Originals are moved to .carbonado-backup and can be restored at any time.",
            Style = (Style)ThemeHarness.Theme["Text.Body"],
            Margin = new Thickness(0, 10, 0, 22),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", Style = (Style)ThemeHarness.Theme["Button.Ghost"], Margin = new Thickness(0, 0, 8, 0) });
        buttons.Children.Add(new Button { Content = "Replace", Style = (Style)ThemeHarness.Theme["Button.Primary"] });
        dialog.Children.Add(buttons);
        return new ModalHost { Content = page, Dialog = dialog, IsOpen = true, Width = 640, Height = 400 };
    }

    private static FrameworkElement Chips()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Chip { Content = "Purezza", Badge = "FL", Margin = new Thickness(4) });
        row.Children.Add(new Chip { Content = "Invisibile", Badge = "VVS", Margin = new Thickness(4) });
        row.Children.Add(new Chip { Content = "Eccellente", Badge = "VS", IsChecked = true, Margin = new Thickness(4) });
        row.Children.Add(new Chip { Content = "Equilibrio", Badge = "SI", IsEnabled = false, Margin = new Thickness(4) });
        return row;
    }

    /// <summary>Synthetic photo: smooth sky gradient, a sun and ridges; posterized = visibly banded "compressed" copy.</summary>
    private static BitmapSource Photo(bool posterize)
    {
        const int w = 320, h = 200;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double t = y / (double)h;
                double sun = Math.Max(0, 1 - (Math.Sqrt(((x - 220) * (x - 220)) + ((y - 70) * (y - 70))) / 46));
                bool ridge = y > 130 + (18 * Math.Sin(x / 23.0)) + (8 * Math.Sin(x / 7.0));
                double r = ridge ? 30 + (20 * t) : 60 + (150 * t) + (180 * sun);
                double g = ridge ? 34 + (24 * t) : 70 + (90 * t) + (150 * sun);
                double b = ridge ? 52 + (30 * t) : 160 - (40 * t) + (60 * sun);
                if (posterize)
                {
                    r = Math.Round(r / 32) * 32;
                    g = Math.Round(g / 32) * 32;
                    b = Math.Round(b / 32) * 32;
                }

                int o = ((y * w) + x) * 4;
                pixels[o] = (byte)Math.Clamp(b, 0, 255);
                pixels[o + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[o + 2] = (byte)Math.Clamp(r, 0, 255);
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Heat-map of |Before − After|: transparent where equal, hot where banding hurts.</summary>
    private static BitmapSource Difference()
    {
        var a = Photo(false);
        var b = Photo(true);
        int w = a.PixelWidth, h = a.PixelHeight;
        var pa = new byte[w * h * 4];
        var pb = new byte[w * h * 4];
        a.CopyPixels(pa, w * 4, 0);
        b.CopyPixels(pb, w * 4, 0);
        var heat = new byte[w * h * 4];
        for (int i = 0; i < pa.Length; i += 4)
        {
            double d = (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2])) / 96.0;
            d = Math.Clamp(d, 0, 1);
            heat[i] = (byte)(255 * (1 - d) * 0.4);
            heat[i + 1] = (byte)(200 * d * d);
            heat[i + 2] = (byte)(255 * Math.Sqrt(d));
            heat[i + 3] = (byte)(230 * d);
        }

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, heat, w * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
