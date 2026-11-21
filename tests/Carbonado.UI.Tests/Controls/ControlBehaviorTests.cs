using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.UI.Controls;
using Carbonado.UI.Tests.TestSupport;
using Carbonado.UI.Tests.Theme;

namespace Carbonado.UI.Tests.Controls;

public class ControlBehaviorTests
{
    [Fact]
    public void CountUpText_ReachesTargetValue() => Sta.RunLoop(async () =>
    {
        var counter = new CountUpText { Duration = new Duration(TimeSpan.FromMilliseconds(250)), FontSize = 32 };
        using var live = new LiveWindow(counter);
        await LiveWindow.Until(() => counter.IsLoaded);

        counter.Value = 12840;
        Assert.True(counter.DisplayValue < 12840, "the count should start from the shown value, not jump");
        await LiveWindow.Until(() => counter.DisplayValue == 12840);
        Assert.Equal("12,840", counter.Text);

        counter.Format = CountFormat.Bytes;
        counter.Value = 3.42 * 1024 * 1024 * 1024;
        await LiveWindow.Until(() => counter.Text == "3.42 GB");
    });

    [Theory]
    [InlineData(CountFormat.Integer, 1284.0, "en-US", "1,284")]
    [InlineData(CountFormat.Integer, 1284.0, "it-IT", "1.284")]
    [InlineData(CountFormat.Score, 87.44, "en-US", "87.4")]
    [InlineData(CountFormat.Score, 87.44, "it-IT", "87,4")]
    [InlineData(CountFormat.Percent, 0.912, "en-US", "91%")]
    [InlineData(CountFormat.Percent, 0.29, "en-US", "29%")]
    [InlineData(CountFormat.Bytes, 512.0, "en-US", "512 B")]
    [InlineData(CountFormat.Bytes, 1536.0, "en-US", "1.50 KB")]
    [InlineData(CountFormat.Bytes, 147_000.0, "en-US", "144 KB")]
    [InlineData(CountFormat.Bytes, 3.42 * 1024 * 1024 * 1024, "it-IT", "3,42 GB")]
    public void Odometer_FormatsPerCulture(CountFormat format, double value, string culture, string expected) =>
        Assert.Equal(expected, Odometer.Format(value, format, CultureInfo.GetCultureInfo(culture).NumberFormat));

    [Fact]
    public void Odometer_PercentFollowsCulturePattern()
    {
        var spaced = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        spaced.PercentPositivePattern = 0;
        Assert.Equal("91 %", Odometer.Format(0.912, CountFormat.Percent, spaced));
    }

    [Fact]
    public void Odometer_RollsLowestDigitAndCarriesOnNines()
    {
        var numbers = CultureInfo.InvariantCulture.NumberFormat;
        var reading = Odometer.Read(199.5, CountFormat.Integer, 200, numbers);
        Assert.Equal("199", Odometer.Text(reading));
        // Every digit sits on a run of nines below it, so all three roll together (199 → 200).
        Assert.All(reading, g => Assert.Equal(0.5, g.Roll, 6));

        var partial = Odometer.Read(123.25, CountFormat.Integer, 200, numbers);
        Assert.Equal([0.0, 0.0, 0.25], partial.Select(g => Math.Round(g.Roll, 6)));
    }

    [Fact]
    public void Gauge_ClampsValue() => Sta.Run(() =>
    {
        var gauge = new RadialGauge { Value = 1.7 };
        Assert.Equal(1.0, gauge.Value);
        gauge.Value = -0.3;
        Assert.Equal(0.0, gauge.Value);
        gauge.Value = double.NaN;
        Assert.Equal(0.0, gauge.Value);
        gauge.Value = 0.42;
        Assert.Equal(0.42, gauge.Value);
        Assert.Equal(0.42, gauge.ShownValue, 9); // not loaded: no animation, drawn value follows at once
    });

    [Fact]
    public void GemRing_ClampsAndFollowsValue() => Sta.Run(() =>
    {
        var ring = new GemProgressRing { Value = 2 };
        Assert.Equal(1.0, ring.Value);
        ring.Value = 0.25;
        Assert.Equal(0.25, ring.ShownValue, 9);
    });

    [Fact]
    public void Viewer_ZoomAroundCursorKeepsPointFixed() => Sta.Run(() =>
    {
        var viewer = new BeforeAfterViewer { Before = Bitmap(800, 600), After = Bitmap(800, 600) };
        var stage = ThemeHarness.Stage(viewer);
        ThemeHarness.Layout(stage, 400, 300);

        // Initially fitted: the whole 800×600 image fills the 400×300 view.
        AssertClose(new Point(400, 300), viewer.ViewToImage(new Point(200, 150)));
        AssertClose(new Point(400, 300), viewer.ImageToView(new Point(800, 600)));

        foreach (var (point, factor) in new[] { (new Point(123, 77), 2.5), (new Point(10, 290), 0.4), (new Point(390, 5), 7.0) })
        {
            var before = viewer.ViewToImage(point);
            viewer.ZoomAt(point, viewer.Zoom * factor);
            var after = viewer.ViewToImage(point);
            Assert.Equal(before.X, after.X, 6);
            Assert.Equal(before.Y, after.Y, 6);
        }

        // Side by side: both panes share zoom and pan, so the same pane-relative point shows the same pixel.
        viewer.Mode = CompareMode.SideBySide;
        ThemeHarness.Layout(stage, 400, 300);
        double paneOffset = (viewer.ActualWidth / 2) + 0.5;
        AssertClose(viewer.ViewToImage(new Point(50, 60)), viewer.ViewToImage(new Point(50 + paneOffset, 60)));
        viewer.ZoomAt(new Point(260, 100), viewer.Zoom * 3);
        AssertClose(viewer.ViewToImage(new Point(260 - paneOffset, 100)), viewer.ViewToImage(new Point(260, 100)));
    });

    [Fact]
    public void Viewer_FitAndActualCommands() => Sta.Run(() =>
    {
        var viewer = new BeforeAfterViewer { After = Bitmap(1000, 500) };
        var stage = ThemeHarness.Stage(viewer);
        ThemeHarness.Layout(stage, 400, 300);
        viewer.ZoomToActualCommand.Execute(null);
        Assert.Equal(1.0, viewer.Zoom, 6);
        viewer.ZoomToFitCommand.Execute(null);
        AssertClose(new Point(0, 50), viewer.Pan);
        AssertClose(new Point(400, 250), viewer.ImageToView(new Point(1000, 500)));
        viewer.Zoom = 1000;
        Assert.Equal(BeforeAfterViewer.MaxZoom, viewer.Zoom);
    });

    [Fact]
    public void DropZone_RaisesFilesDropped() => Sta.Run(() =>
    {
        var zone = new DropZone();
        IReadOnlyList<string>? dropped = null;
        zone.FilesDropped += (_, e) => dropped = e.Paths;
        string[] paths = [@"C:\Photos\aurora.png", @"C:\Photos\Ritratti — 2026\carbon.jpg", @"D:\Scans"];

        Assert.True(zone.AcceptDrop(new DataObject(DataFormats.FileDrop, paths)));
        Assert.Equal(paths, dropped);
        Assert.False(zone.IsDragOver);

        dropped = null;
        Assert.False(zone.AcceptDrop(new DataObject(DataFormats.UnicodeText, "not files")));
        Assert.Null(dropped);
    });

    [Fact]
    public void Particles_DeterministicForSeed() => Sta.Run(() =>
    {
        ParticleField Make(int seed)
        {
            var field = new ParticleField { Seed = seed, Count = 64, IsRunning = false };
            field.Advance(TimeSpan.FromSeconds(1.5));
            return field;
        }

        var a = Make(7);
        var b = Make(7);
        var c = Make(8);
        Assert.Equal(a.Positions, b.Positions);
        Assert.NotEqual(a.Positions, c.Positions);
        Assert.Equal(64, a.Positions.Count);
        Assert.Equal(1.5, a.Time, 2);

        // The attractor gathers the dust: mean distance to it shrinks.
        var drawn = new ParticleField { Seed = 7, Count = 64, IsRunning = false, Intensity = 1, Attractor = new Point(0.5, 0.5) };
        double Spread(ParticleField f) => f.Positions.Average(p => (p - new Point(0.5, 0.5)).Length);
        double start = Spread(drawn);
        drawn.Advance(TimeSpan.FromSeconds(2));
        Assert.True(Spread(drawn) < start * 0.9, $"attractor had no effect ({start:F3} → {Spread(drawn):F3})");
    });

    [Fact]
    public void Donut_SlicesSumTo360() => Sta.Run(() =>
    {
        var brush = Brushes.Transparent;
        var slices = new[]
        {
            new DonutSlice("JXL", 52, brush), new DonutSlice("AVIF", 31, brush), new DonutSlice("WebP", 12.5, brush),
            new DonutSlice("PNG", 4.5, brush), new DonutSlice("none", 0, brush), new DonutSlice("bad", double.NaN, brush),
        };
        var sweeps = DonutChart.ComputeSweeps(slices);
        Assert.Equal(360, sweeps.Sum(), 9);
        Assert.Equal(52 * 3.6, sweeps[0], 9);
        Assert.Equal(0, sweeps[4]);
        Assert.Equal(0, sweeps[5]);
        Assert.All(DonutChart.ComputeSweeps([new DonutSlice("x", 0, brush)]), s => Assert.Equal(0, s));
    });

    [Fact]
    public void PrismButton_BusySwallowsClicks_AndRipplesFromCenterOnKeyboard() => Sta.Run(() =>
    {
        var button = new PrismButton { Content = "Start", Width = 160, Height = 42 };
        var stage = ThemeHarness.Stage(button);
        ThemeHarness.Layout(stage, 200, 80);
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;

        // The automation peer clicks asynchronously (dispatcher), like a real user click.
        invoke.Invoke();
        Sta.DoEvents();
        Assert.Equal(1, clicks);
        button.IsBusy = true;
        invoke.Invoke();
        Sta.DoEvents();
        Assert.Equal(1, clicks);

        button.StartRipple(new Point(30, 12));
        Assert.Equal(new Point(30, 12), button.LastRippleOrigin);
    });

    [Fact]
    public void ToastHost_ShowsAndDismissesItself() => Sta.RunLoop(async () =>
    {
        var host = new ToastHost { DismissAfter = TimeSpan.FromMilliseconds(250), MaxToasts = 2 };
        using var live = new LiveWindow(host, 420, 400);
        await LiveWindow.Until(() => host.IsLoaded);

        host.Show("Done", "12 files compressed", ToastKind.Success);
        host.Show("Kept", "1 original kept", ToastKind.Warning);
        host.Show("Error", "1 file failed", ToastKind.Error);
        await LiveWindow.Until(() => host.Items.Count == 2); // the oldest leaves to respect MaxToasts
        await LiveWindow.Until(() => host.Items.Count == 0); // the rest time out
    });

    [Fact]
    public void ModalHost_OpensOverContent_EscapeCloses() => Sta.RunLoop(async () =>
    {
        var accept = new Button { Content = "OK" };
        var host = new ModalHost { Content = new Button { Content = "Page" }, Dialog = accept };
        using var live = new LiveWindow(host);
        await LiveWindow.Until(() => host.IsLoaded);
        var scrim = (FrameworkElement)host.Template.FindName("PART_Scrim", host);
        var dialog = (FrameworkElement)host.Template.FindName("PART_Dialog", host);
        Assert.Equal(Visibility.Collapsed, dialog.Visibility);

        host.IsOpen = true;
        Assert.Equal(Visibility.Visible, scrim.Visibility);
        Assert.Equal(Visibility.Visible, dialog.Visibility);
        await LiveWindow.Until(() => FocusManager.GetFocusedElement(live.Window) == accept);

        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        accept.RaiseEvent(escape);
        Assert.False(host.IsOpen);
        await LiveWindow.Until(() => dialog.Visibility == Visibility.Collapsed);
    });

    [Fact]
    public void NavRail_IndicatorFollowsSelection() => Sta.RunLoop(async () =>
    {
        var rail = new NavRail();
        foreach (string label in new[] { "Forge", "Compare", "Profiles", "Archive" })
        {
            rail.Items.Add(new NavItem { Label = label, Shortcut = "Ctrl+1" });
        }

        using var live = new LiveWindow(rail, 120, 400);
        await LiveWindow.Until(() => rail.IsLoaded);
        rail.SelectedIndex = 2;
        var item = (NavItem)rail.Items[2];
        await LiveWindow.Until(() => item.IsArrangeValid && rail.IndicatorTarget > 0);
        double expected = item.TranslatePoint(new Point(0, item.ActualHeight / 2), rail).Y;
        var indicator = (FrameworkElement)rail.Template.FindName("PART_Indicator", rail);
        await LiveWindow.Until(() =>
            Math.Abs(indicator.TranslatePoint(new Point(0, indicator.ActualHeight / 2), rail).Y - expected) < 0.5);
        Assert.Equal("Profiles   Ctrl+1", item.ToolTip);
    });

    private static void AssertClose(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    private static BitmapSource Bitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                pixels[o] = (byte)(x * 255 / width);
                pixels[o + 1] = (byte)(y * 255 / height);
                pixels[o + 2] = 128;
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
