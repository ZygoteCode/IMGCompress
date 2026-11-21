using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Carbonado.UI.Tests.TestSupport;
using static Carbonado.UI.Tests.Theme.ThemeSpecimens;
using IconPresenter = Carbonado.UI.Controls.IconPresenter;

namespace Carbonado.UI.Tests.Theme;

public partial class ThemeSnapshotTests
{
    private const string Suite = "ui/theme";

    /// <summary>Pixels that must change for a state to count as visible (a checkbox hover edge is ~100 px).</summary>
    private const int MinVisibleChange = 30;

    public static TheoryData<string, string> StyleCases => Cases();

    [Theory]
    [MemberData(nameof(StyleCases))]
    public void EveryStyle_RendersInAllStates(string name, string state) => Sta.Run(() =>
    {
        var specimen = All[name];
        var shot = RenderSpecimen(name, state, save: true);
        Assert.True(Snapshot.DistinctColors(shot.Bitmap, 2) > 2, $"{name} ({state}) rendered blank");
        if (specimen.Opaque && shot.Stage is { } stage && shot.Target is { } target)
        {
            var probe = target.TranslatePoint(new Point(4, target.ActualHeight / 2), stage);
            byte alpha = ThemeHarness.AlphaAt(stage, specimen.Width, specimen.Height, probe);
            Assert.True(alpha > 0, $"{name} ({state}) has no background at {probe}");
        }

        if (state == Normal)
        {
            return;
        }

        // Every state must actually look different from Normal (a broken storyboard or trigger would not).
        var normal = RenderSpecimen(name, Normal, save: false);
        int changed = ThemeHarness.CountDifferent(shot.Bitmap, normal.Bitmap);
        Assert.True(changed >= MinVisibleChange, $"{name}: {state} is indistinguishable from Normal ({changed} px differ)");

        // Outer focus rings are drawn around the control: the band just outside its bounds must light up.
        if (state == Focused && shot.Target is { FocusVisualStyle: { } focus } focused
            && !ReferenceEquals(focus, ThemeHarness.Theme["Focus.Prisma.Inset"]))
        {
            var origin = focused.TranslatePoint(default, shot.Stage!);
            var bounds = new Rect(origin, focused.RenderSize);
            int ring = ThemeHarness.CountDifferentOutside(shot.Bitmap, normal.Bitmap, bounds, band: 6);
            Assert.True(ring >= MinVisibleChange, $"{name}: no focus ring around the control ({ring} px changed outside it)");
        }
    });

    /// <summary>Renders one specimen in one state; saves the PNG when <paramref name="save"/>.</summary>
    private static SpecimenShot RenderSpecimen(string name, string state, bool save)
    {
        var specimen = All[name];
        var (root, target) = specimen.Create();
        if (target is not null)
        {
            Prepare(target, state);
        }

        if (specimen.IsRoot)
        {
            // ToolTip/ContextMenu refuse parents: render them as roots (the popup card is their whole visual).
            var control = (Control)root;
            control.Margin = new Thickness(16, 12, 0, 0);
            if (target is MenuItem item)
            {
                control.ApplyTemplate();
                ThemeHarness.Layout(control, specimen.Width, specimen.Height);
                ApplyMenuState(item, state);
            }

            return new SpecimenShot(ThemeHarness.RenderRoot(control, specimen.Width, specimen.Height, save ? Suite : null, $"{name}.{state}"), null, target);
        }

        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.VerticalAlignment = VerticalAlignment.Center;
        var stage = ThemeHarness.Stage(root);
        ThemeHarness.Layout(stage, specimen.Width, specimen.Height);
        if (target is not null)
        {
            Apply(stage, target, state);
        }

        var bitmap = save
            ? ThemeHarness.Render(stage, specimen.Width, specimen.Height, Suite, $"{name}.{state}")
            : Snapshot.Render(stage, specimen.Width, specimen.Height, theme: ThemeHarness.Theme);
        return new SpecimenShot(bitmap, stage, target);
    }

    [Fact]
    public void Window_RendersBackdropAndContent() => Sta.Run(() =>
    {
        var window = new Window
        {
            Width = 480,
            Height = 300,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new TextBlock { Text = "Carbonado", Style = (Style)ThemeHarness.Theme["Text.Display"], Margin = new Thickness(32) },
        };
        window.Resources.MergedDictionaries.Add(ThemeHarness.Theme);
        window.Style = (Style)ThemeHarness.Theme[typeof(Window)];
        window.Show();
        try
        {
            Sta.DoEvents();
            window.UpdateLayout();
            // The window's own visual is hosted by its HwndSource; its template root is what paints the client area.
            var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
            int width = (int)client.ActualWidth, height = (int)client.ActualHeight;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(client);
            bitmap.Freeze();
            ThemeHarness.Save(bitmap, Path.Combine(Snapshot.ArtifactsDir(Suite), "Window.Normal.png"));
            // Iris bloom top-right is brighter than the plain obsidian bottom-right corner.
            var bloom = Snapshot.PixelAt(bitmap, width - 10, 8);
            var plain = Snapshot.PixelAt(bitmap, width - 10, height - 8);
            Assert.True(bloom.B > plain.B, $"no backdrop bloom ({bloom} vs {plain})");
            Assert.True(Snapshot.DistinctColors(bitmap, 2) > 20, "window rendered flat");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void PrimaryButton_HoverSheenSweeps() => Sta.Run(() =>
    {
        var button = new Button { Content = "Start forging", Style = (Style)ThemeHarness.Theme["Button.Primary"], Width = 180 };
        var stage = ThemeHarness.Stage(button);
        ThemeHarness.Layout(stage, 220, 80);
        ThemeHarness.GoTo(button, "MouseOver", TimeSpan.FromMilliseconds(220));
        var midSweep = ThemeHarness.Render(stage, 220, 80, Suite, "Button.Primary.HoverSheen");
        ThemeHarness.Settle(button);
        var settled = Snapshot.Render(stage, 220, 80, theme: ThemeHarness.Theme);

        // Mid-sweep the band brightens part of the gradient; once settled it has left the button.
        int brighter = 0;
        for (int x = 30; x < 190; x += 2)
        {
            var a = Snapshot.PixelAt(midSweep, x, 30);
            var b = Snapshot.PixelAt(settled, x, 30);
            brighter += a.R + a.G + a.B > b.R + b.G + b.B + 30 ? 1 : 0;
        }

        Assert.True(brighter > 5, $"sheen not visible mid-sweep ({brighter} brighter samples)");
    });

    [Fact]
    public void Icons_AllKeysResolveToGeometry() => Sta.Run(() =>
    {
        var icons = Snapshot.LoadDictionary("Theme/Icons.xaml");
        var keys = icons.Keys.OfType<string>().ToList();
        Assert.True(keys.Count >= 60, $"only {keys.Count} icons");
        var grid = new Rect(0, 0, 20, 20);
        foreach (string key in keys)
        {
            Assert.StartsWith("Icon.", key);
            var geometry = Assert.IsAssignableFrom<Geometry>(icons[key]);
            Assert.True(geometry.IsFrozen, $"{key} is not frozen");
            Assert.False(geometry.Bounds.IsEmpty, $"{key} is empty");
            Assert.True(grid.Contains(geometry.Bounds), $"{key} leaves the 20×20 grid: {geometry.Bounds}");
        }

        // The theme exposes the same keys, and every one renders as an icon sheet for review.
        var theme = ThemeHarness.Theme;
        Assert.All(keys, key => Assert.IsAssignableFrom<Geometry>(theme[key]));
        RenderIconSheet(keys);
    });

    [Fact]
    public void Tokens_NoLiteralHexOutsideTokens()
    {
        string uiRoot = Path.Combine(RepoRoot(), "src", "Carbonado.UI");
        var offenders = new List<string>();
        var namedColors = typeof(Colors).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file).Equals("Tokens.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            offenders.AddRange(HexLiteral().Matches(text).Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
            offenders.AddRange(ColorAttribute().Matches(text)
                .Where(m => namedColors.Contains(m.Groups["value"].Value))
                .Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
        }

        foreach (string folder in new[] { "Controls", "Transitions", "Theme" })
        {
            string dir = Path.Combine(uiRoot, folder);
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                offenders.AddRange(CodeColor().Matches(File.ReadAllText(file)).Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
            }
        }

        Assert.True(offenders.Count == 0, "Literal colors outside Tokens.xaml:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Theme_LoadsUnder50ms()
    {
        // 1) First XAML of the process: mostly WPF's own BAML/JIT start-up, paid by any app regardless of the theme.
        var framework = Sta.Run(() => Measure(() => _ = Snapshot.LoadDictionary("Theme/Tokens.xaml")["B.Window"]));
        // 2) First Theme.xaml on a fresh UI thread (nothing shared yet): what the app pays at start-up.
        var first = Sta.Run(() => Measure(Load));
        // 3) Again on another fresh thread: steady-state cost of the theme files themselves.
        var warm = Sta.Run(() => Measure(Load));
        var realize = Sta.Run(() =>
        {
            var theme = Snapshot.LoadDictionary(ThemeHarness.ThemePath);
            return Measure(() => RealizeStyles(theme));
        });
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"First XAML in process {framework.TotalMilliseconds:F1} ms; Theme.xaml first {first.TotalMilliseconds:F1} ms, " +
            $"warm {warm.TotalMilliseconds:F1} ms; realizing every resource {realize.TotalMilliseconds:F1} ms");
        Assert.True(warm < TimeSpan.FromMilliseconds(50), $"Theme.xaml took {warm.TotalMilliseconds:F1} ms to load");

        static void Load() => _ = Snapshot.LoadDictionary(ThemeHarness.ThemePath)["Button.Primary"];
    }

    private static void ApplyMenuState(MenuItem item, string state)
    {
        switch (state)
        {
            case Hover:
                // IsHighlighted has a protected setter; the menu sets it on pointer/keyboard navigation.
                typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(item, true);
                break;
            case Checked:
                item.IsChecked = true;
                break;
        }
    }

    private static void Prepare(Control target, string state)
    {
        switch (state)
        {
            case Disabled:
                target.IsEnabled = false;
                break;
            case Indeterminate when target is ProgressBar progress:
                progress.IsIndeterminate = true;
                break;
            case Checked when target is ToggleButton toggle:
                toggle.IsChecked = true;
                break;
            case Checked when target is ListBoxItem item:
                item.IsSelected = true;
                break;
            case Checked when target is Expander expander:
                expander.IsExpanded = true;
                break;
        }
    }

    private static void Apply(Grid stage, Control target, string state)
    {
        switch (state)
        {
            case Hover:
                ThemeHarness.GoTo(target, "MouseOver");
                break;
            case Pressed:
                ThemeHarness.GoTo(target, "Pressed");
                break;
            case Disabled:
                // Some templates (GroupBox, ProgressBar) express IsEnabled with triggers instead of a state.
                VisualStateManager.GoToState(target, "Disabled", false);
                break;
            case Checked when target is Expander:
                // Expansion is property-driven (IsExpanded trigger + header toggle state).
                break;
            case Checked:
                ThemeHarness.GoTo(target, target is ListBoxItem ? "Selected" : "Checked");
                break;
            case Focused:
                VisualStateManager.GoToState(target, "Focused", false);
                ThemeHarness.ShowFocusVisual(stage, target);
                ThemeHarness.Layout(stage, (int)stage.ActualWidth, (int)stage.ActualHeight);
                break;
            case Indeterminate:
                // ProgressBar only enters Indeterminate while visible (in a window); force it, park the band mid-sweep.
                ThemeHarness.GoTo(target, "Indeterminate", TimeSpan.FromSeconds(0.65));
                return;
        }

        ThemeHarness.Settle(target);
    }

    private static void RealizeStyles(ResourceDictionary theme)
    {
        foreach (var dictionary in Flatten(theme))
        {
            foreach (object key in dictionary.Keys)
            {
                _ = dictionary[key];
            }
        }
    }

    private static IEnumerable<ResourceDictionary> Flatten(ResourceDictionary dictionary) =>
        dictionary.MergedDictionaries.SelectMany(Flatten).Append(dictionary);

    private static TimeSpan Measure(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    private static void RenderIconSheet(IReadOnlyList<string> keys)
    {
        const int cell = 56;
        int columns = 10;
        int rows = (keys.Count + columns - 1) / columns;
        var grid = new UniformGrid { Columns = columns, Rows = rows };
        foreach (string key in keys)
        {
            var icon = new IconPresenter { Icon = (Geometry)ThemeHarness.Theme[key], Width = 24, Height = 24 };
            icon.SetResourceReference(IconPresenter.ForegroundProperty, "B.Text");
            grid.Children.Add(icon);
        }

        var stage = ThemeHarness.Stage(grid);
        ThemeHarness.Layout(stage, columns * cell, rows * cell);
        var bitmap = ThemeHarness.Render(stage, columns * cell, rows * cell, Suite, "Icons.Sheet");
        Assert.True(Snapshot.DistinctColors(bitmap, 1) > 10);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Carbonado.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Carbonado.sln not found above the test binaries.");
    }

    private sealed record SpecimenShot(System.Windows.Media.Imaging.BitmapSource Bitmap, Grid? Stage, Control? Target);

    [GeneratedRegex(@"#[0-9A-Fa-f]{3,8}\b")]
    private static partial Regex HexLiteral();

    [GeneratedRegex(@"(?:Background|Foreground|Fill|Stroke|BorderBrush|Color|OpacityMask)=""(?<value>[A-Za-z]+)""")]
    private static partial Regex ColorAttribute();

    [GeneratedRegex(@"\b(?:Colors\.[A-Z]\w*|Brushes\.[A-Z]\w*|Color\.FromA?Rgb\s*\()")]
    private static partial Regex CodeColor();
}
