using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Carbonado.UI.Brand;
using Carbonado.UI.Cinematics.Intro;
using Carbonado.UI.Motion;
using Carbonado.UI.Tests.TestSupport;

namespace Carbonado.UI.Tests.Cinematics;

/// <summary>Intro tests read or change the global <see cref="MotionSettings"/>: they run alone, after the parallel phase.</summary>
[Collection(MotionSettingsCollection.Name)]
public class IntroTests
{
    private const int Width = 1280;
    private const int Height = 800;
    private const string TaglineText = "Infinite pressure. Flawless brilliance.";
    private static readonly double[] s_keyTimes = [0.2, 0.8, 1.3, 1.6, 2.2, 2.6, 3.0, 3.35];

    [Fact]
    public void Intro_RenderAtKeyTimes_Snapshots() => Staged((stage, theme) =>
    {
        string dir = Snapshot.ArtifactsDir("ui/intro");
        foreach (double t in s_keyTimes)
        {
            stage.Overlay.RenderAt(t);
            string file = Path.Combine(dir, $"intro-{t.ToString("0.00", CultureInfo.InvariantCulture)}.png");
            BitmapSource frame = Snapshot.Render(stage.Host, Width, Height, file, theme);
            Assert.True(Snapshot.DistinctColors(frame) > 48, $"frame at {t}s is nearly uniform");
        }
    });

    [Fact]
    public void Intro_SameTimeSameFrame() => Staged((stage, theme) =>
    {
        foreach (double t in new[] { 0.8, 1.3, 2.2, 3.35 })
        {
            stage.Overlay.RenderAt(t);
            byte[] first = Pixels(Snapshot.Render(stage.Host, Width, Height, null, theme));
            stage.Overlay.RenderAt(t * 0.37);
            stage.Overlay.RenderAt(3.4 - t);
            stage.Overlay.RenderAt(t);
            byte[] again = Pixels(Snapshot.Render(stage.Host, Width, Height, null, theme));
            Assert.True(first.AsSpan().SequenceEqual(again), $"re-rendering {t}s changed pixels");

            Stage fresh = Stage.Create();
            fresh.Overlay.RenderAt(t);
            byte[] other = Pixels(Snapshot.Render(fresh.Host, Width, Height, null, theme));
            Assert.True(first.AsSpan().SequenceEqual(other), $"a fresh overlay renders {t}s differently");
        }
    });

    [Fact]
    public void Intro_SkipCompletesImmediately() => WithMotion(reducedMotion: false, () => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var overlay = new IntroOverlay();
        int completed = 0, handOff = 0;
        overlay.Completed += (_, _) => completed++;
        overlay.HandOffStarted += (_, _) => handOff++;

        overlay.Play();
        Assert.True(overlay.IsPlaying);
        overlay.Skip();

        Assert.Equal(1, completed);
        Assert.Equal(1, handOff);
        Assert.False(overlay.IsPlaying);
        Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        Assert.False(overlay.IsHitTestVisible);
        Assert.Null(Find<Diamond3D>(overlay));
        overlay.Skip();
        Assert.Equal(1, completed);

        // Replay works and can be skipped again.
        overlay.Play();
        Assert.Equal(Visibility.Visible, overlay.Visibility);
        overlay.Skip();
        Assert.Equal(2, completed);
    }));

    [Fact]
    public void Intro_EscapeSpaceOrClickSkip() => WithMotion(reducedMotion: false, () => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var overlay = new IntroOverlay();
        var window = new Window
        {
            Content = overlay,
            Width = 320,
            Height = 200,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        int completed = 0;
        overlay.Completed += (_, _) => completed++;
        try
        {
            window.Show();
            Sta.DoEvents();
            PresentationSource source = PresentationSource.FromVisual(window)!;

            // Keys reach the overlay through the window, wherever focus is; other keys pass through.
            foreach (Key key in new[] { Key.Escape, Key.Space })
            {
                overlay.Play();
                var other = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(other);
                Assert.False(other.Handled);
                Assert.True(overlay.IsPlaying);

                var skip = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(skip);
                Assert.True(skip.Handled, $"{key} was not handled");
                Assert.False(overlay.IsPlaying);
            }

            overlay.Play();
            overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent });
            Assert.False(overlay.IsPlaying);
            Assert.Equal(3, completed);

            // Once completed, the window's keys are no longer intercepted.
            var after = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(after);
            Assert.False(after.Handled);

            // From the hand-off on the shell is interactive: Esc and Space are its own while the intro finishes.
            overlay.Play();
            overlay.RenderAt(3.0);
            var handOff = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(handOff);
            Assert.False(handOff.Handled);
            Assert.True(overlay.IsPlaying);
            overlay.Skip();
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void Intro_RenderFault_EndsTheIntroAndGetsOutOfTheWay() => WithMotion(reducedMotion: false, () => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var logo = new FaultyLogo();
        var overlay = new IntroOverlay { LogoTarget = logo };
        int completed = 0;
        overlay.Completed += (_, _) => completed++;

        // A frame that throws while starting: the intro ends at once instead of staying over the app.
        logo.FailNextOpacityChange();
        Assert.Throws<InvalidOperationException>(overlay.Play);
        Assert.Equal(1, completed);
        Assert.False(overlay.IsPlaying);
        Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        Assert.Equal(1, logo.Opacity);

        // A final frame that throws while skipping: the run still completes and the overlay still collapses.
        overlay.Play();
        Assert.True(overlay.IsPlaying);
        logo.FailNextOpacityChange();
        Assert.Throws<InvalidOperationException>(overlay.Skip);
        Assert.Equal(2, completed);
        Assert.False(overlay.IsPlaying);
        Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        Assert.Equal(1, logo.Opacity);
    }));

    [Fact]
    public void Intro_ShortModeDuration() => WithMotion(reducedMotion: false, () => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        Assert.Equal(3.4, new IntroOverlay { Mode = IntroMode.Cinematic }.Duration);
        Assert.Equal(0.9, new IntroOverlay { Mode = IntroMode.Short }.Duration);
        Assert.Equal(IntroMode.Short, new IntroOverlay { Mode = IntroMode.Short }.EffectiveMode);

        var off = new IntroOverlay { Mode = IntroMode.Off };
        Assert.Equal(0, off.Duration);
        int completed = 0;
        off.Completed += (_, _) => completed++;
        off.Play();
        Assert.Equal(1, completed);
        Assert.False(off.IsPlaying);
        Assert.Equal(Visibility.Collapsed, off.Visibility);
    }));

    [Fact]
    public void Intro_ReducedMotion_UsesShort() => WithMotion(reducedMotion: true, () => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        var overlay = new IntroOverlay { Mode = IntroMode.Cinematic };
        Assert.Equal(IntroMode.Short, overlay.EffectiveMode);
        Assert.Equal(0.9, overlay.Duration);

        int completed = 0;
        overlay.Completed += (_, _) => completed++;
        overlay.Play();
        overlay.Skip();
        Assert.Equal(1, completed);
    }));

    [Fact]
    public void Intro_ReducedMotion_OnlyFades() => WithMotion(reducedMotion: true, () => Staged((stage, theme) =>
    {
        string dir = Snapshot.ArtifactsDir("ui/intro");
        foreach (double t in new[] { 0.3, 0.7 })
        {
            stage.Overlay.RenderAt(t);
            BitmapSource frame = Snapshot.Render(stage.Host, Width, Height, Path.Combine(dir, $"reduced-{t.ToString("0.00", CultureInfo.InvariantCulture)}.png"), theme);
            Assert.True(Snapshot.DistinctColors(frame) > 24);
        }

        // No flight: the diamond never leaves the core while it fades out.
        stage.Overlay.RenderAt(0.8);
        Diamond3D diamond = FindChild<Diamond3D>(stage.Overlay);
        Assert.True(diamond.RenderTransform.Value.IsIdentity);
    }, applyMotion: false));

    [Fact]
    public void Intro_HandOff_FliesTheDiamondOntoTheLogo() => Staged((stage, _) =>
    {
        Snapshot.Render(stage.Host, Width, Height);
        Rect logo = stage.Logo.TransformToVisual(stage.Overlay).TransformBounds(new Rect(stage.Logo.RenderSize));
        var layout = new IntroLayout(Width, Height);

        // The 3D stage is built on first need: nothing before the crystallization asks for it.
        stage.Overlay.RenderAt(1.0);
        Assert.Null(Find<Diamond3D>(stage.Overlay));
        stage.Overlay.RenderAt(2.5);
        Diamond3D diamond = FindChild<Diamond3D>(stage.Overlay);
        Assert.True(diamond.RenderTransform.Value.IsIdentity, "the diamond moved before the hand-off");

        stage.Overlay.RenderAt(3.33);
        Matrix landed = diamond.RenderTransform.Value;
        Point center = landed.Transform(layout.Core);
        Assert.Equal(logo.X + (logo.Width / 2), center.X, 0.5);
        Assert.Equal(logo.Y + (logo.Height / 2), center.Y, 0.5);
        Assert.Equal(diamond.LandingScale(layout, logo), landed.M11, 1e-6);
    });

    [Fact]
    public void Intro_LogoTarget_HiddenUntilLandingThenRestored() => Staged((stage, theme) =>
    {
        stage.Logo.Glint = 0.2;
        stage.Overlay.RenderAt(1.0);
        Assert.Equal(0, stage.Logo.Opacity);
        stage.Overlay.RenderAt(3.3);
        Assert.InRange(stage.Logo.Opacity, 0.05, 0.95);
        Assert.True(stage.Logo.Glint > 0.2);

        stage.Overlay.RenderAt(3.4);
        Assert.Equal(DependencyProperty.UnsetValue, stage.Logo.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Equal(1, stage.Logo.Opacity);
        Assert.Equal(0.2, stage.Logo.Glint);
    });

    [Fact]
    public void Intro_ScalesWithTheWindow() => Staged((stage, theme) =>
    {
        string dir = Snapshot.ArtifactsDir("ui/intro");
        foreach (var (w, h) in new[] { (800, 500), (1920, 1080), (640, 900), (2560, 1080) })
        {
            stage.Overlay.RenderAt(2.75);
            BitmapSource frame = Snapshot.Render(stage.Host, w, h, Path.Combine(dir, $"size-{w}x{h}.png"), theme);
            Rect bright = BrightBounds(frame, 150);
            Assert.False(bright.IsEmpty, $"{w}x{h}: nothing bright");
            Assert.Equal(w / 2.0, bright.X + (bright.Width / 2), w * 0.04);
            Assert.True(bright.Left > w * 0.02 && bright.Right < w * 0.98, $"{w}x{h}: composition spills sideways");
            Assert.True(bright.Width > w * 0.2, $"{w}x{h}: composition too small");
        }
    });

    [Fact]
    public void Intro_PlaysInRealTimeAndCompletes() => WithMotion(reducedMotion: false, () => Sta.RunLoop(async () =>
    {
        Snapshot.EnsurePackScheme();
        double previous = MotionSettings.Speed;
        MotionSettings.Speed = 6;
        try
        {
            var overlay = new IntroOverlay();
            var done = new TaskCompletionSource();
            overlay.Completed += (_, _) => done.TrySetResult();
            overlay.Play();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        }
        finally
        {
            MotionSettings.Speed = previous;
        }
    }));

    [Fact]
    public void Intro_RenderAt_PerFrameCostIsLow() => Staged((stage, theme) =>
    {
        Snapshot.Render(stage.Host, Width, Height, null, theme);

        // 60 fps sweeps of the whole intro, timing RenderAt plus the layout pass it triggers (WPF composition is
        // excluded). The first sweep is a cold first play (JIT, lazily created pens, first GCs); the second is steady.
        foreach (string pass in new[] { "cold", "warm" })
        {
            int frames = 0;
            double total = 0, worst = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (double t = 0; t <= 3.4; t += 1 / 60.0, frames++)
            {
                long start = Stopwatch.GetTimestamp();
                stage.Overlay.RenderAt(t);
                stage.Host.UpdateLayout();
                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                worst = Math.Max(worst, ms);
                total += ms;
            }

            double average = total / frames;
            double kilobytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0 / frames;
            TestContext.Current.TestOutputHelper?.WriteLine($"RenderAt at {Width}x{Height} ({pass}): average {average:0.000} ms, worst {worst:0.000} ms, {kilobytes:0} KB allocated per frame over {frames} frames");
            Assert.True(average < 8, $"{pass} average RenderAt {average:0.00} ms exceeds the 8 ms budget");
        }
    });

    /// <summary>Review sheets: every 0.1 s of the cinematic intro and every 0.05 s of the short one.</summary>
    [Fact]
    public void Intro_ContactSheets() => Staged((stage, theme) =>
    {
        foreach (var (mode, step, file) in new[] { (IntroMode.Cinematic, 0.1, "contact-sheet.png"), (IntroMode.Short, 0.05, "contact-sheet-short.png") })
        {
            const int cell = 320, cellHeight = 200, columns = 6;
            stage.Overlay.Mode = mode;
            double duration = stage.Overlay.Duration;
            double[] times = Enumerable.Range(0, (int)Math.Round(duration / step) + 2).Select(i => Math.Min(duration, i * step)).ToArray();
            var sheet = new WrapPanel { Width = columns * cell };
            foreach (double t in times)
            {
                stage.Overlay.RenderAt(t);
                sheet.Children.Add(new Image { Source = Snapshot.Render(stage.Host, cell * 2, cellHeight * 2, null, theme), Width = cell, Height = cellHeight });
            }

            int rows = (times.Length + columns - 1) / columns;
            BitmapSource contact = Snapshot.Render(sheet, columns * cell, rows * cellHeight, Path.Combine(Snapshot.ArtifactsDir("ui/intro"), file), theme);
            Assert.True(Snapshot.DistinctColors(contact) > 64, $"{mode} sheet is nearly uniform");
        }
    });

    [Fact]
    public void Mesh_IsClosedAndOutwardNormals()
    {
        foreach (int segments in new[] { 6, 8, 16, 32 })
        {
            MeshGeometry3D mesh = BrilliantCutMesh.Create(segments);
            Assert.True(mesh.IsFrozen);
            Point3DCollection p = mesh.Positions;
            Int32Collection idx = mesh.TriangleIndices;
            Assert.Equal(p.Count, mesh.Normals.Count);
            Assert.Equal(0, idx.Count % 3);

            // Closed and consistently wound: every directed edge (welded by position) meets its reverse exactly once.
            var directed = new Dictionary<(WeldKey, WeldKey), int>();
            for (int t = 0; t < idx.Count; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    var edge = (WeldKey.Of(p[idx[t + e]]), WeldKey.Of(p[idx[t + ((e + 1) % 3)]]));
                    directed[edge] = directed.GetValueOrDefault(edge) + 1;
                }
            }

            foreach (var ((a, b), count) in directed)
            {
                Assert.Equal(1, count);
                Assert.True(directed.ContainsKey((b, a)), $"{segments}: open edge");
            }

            // Outward, flat-shaded and convex: normals follow the winding, point away from the center, and every
            // vertex lies behind every facet plane. Signed volume is positive.
            Point3D center = Centroid(p);
            double volume = 0;
            bool table = false, culet = false;
            for (int t = 0; t < idx.Count; t += 3)
            {
                Point3D a = p[idx[t]], b = p[idx[t + 1]], c = p[idx[t + 2]];
                Vector3D n = Vector3D.CrossProduct(b - a, c - a);
                Assert.True(n.Length > 1e-9, $"{segments}: degenerate triangle");
                n.Normalize();
                Point3D mid = new((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                Assert.True(Vector3D.DotProduct(n, mid - center) > 0, $"{segments}: inward facet");
                for (int k = 0; k < 3; k++)
                {
                    Assert.True((mesh.Normals[idx[t + k]] - n).Length < 1e-9, $"{segments}: normal is not the facet normal");
                }

                foreach (Point3D q in p)
                {
                    Assert.True(Vector3D.DotProduct(n, q - a) < 1e-9, $"{segments}: concave at a facet");
                }

                volume += Vector3D.DotProduct((Vector3D)a, Vector3D.CrossProduct((Vector3D)b, (Vector3D)c)) / 6;
                table |= n.Y > 0.999999;
                culet |= n.Y < -0.999999;
            }

            Assert.True(volume > 0, $"{segments}: negative volume");
            Assert.True(table && culet, $"{segments}: missing table or culet");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => BrilliantCutMesh.Create(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrilliantCutMesh.Create(4));
    }

    [Fact]
    public void Wordmark_HasNineLetters()
    {
        IReadOnlyList<Geometry> letters = WordmarkGeometry.Letters;
        Assert.Equal(9, letters.Count);
        Assert.Equal("CARBONADO", WordmarkGeometry.Text);
        Assert.Same(letters[1], letters[6]);
        Assert.Same(letters[4], letters[8]);
        Assert.Equal(7, letters.Distinct().Count());
        Assert.All(letters, letter => Assert.False(letter.IsEmpty()));
    }

    private static void Staged(Action<Stage, ResourceDictionary> body, bool applyMotion = true)
    {
        void Run() => Sta.Run(() =>
        {
            Snapshot.EnsurePackScheme();
            body(Stage.Create(), Snapshot.LoadDictionary("Theme/Tokens.xaml"));
        });

        if (applyMotion)
        {
            WithMotion(reducedMotion: false, Run);
        }
        else
        {
            Run();
        }
    }

    private static void WithMotion(bool reducedMotion, Action body)
    {
        MotionSettings.SetReducedMotion(reducedMotion);
        try
        {
            body();
        }
        finally
        {
            MotionSettings.SetReducedMotion(null);
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    /// <summary>Bounds of the pixels whose brightest channel exceeds <paramref name="threshold"/>.</summary>
    private static Rect BrightBounds(BitmapSource bitmap, byte threshold)
    {
        byte[] px = Pixels(bitmap);
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                if (Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])) > threshold)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static T FindChild<T>(DependencyObject parent)
        where T : DependencyObject =>
        Find<T>(parent) ?? throw new InvalidOperationException($"{typeof(T).Name} not found");

    private static T? Find<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Find<T>(child)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static Point3D Centroid(Point3DCollection points)
    {
        double x = 0, y = 0, z = 0;
        foreach (Point3D q in points)
        {
            x += q.X;
            y += q.Y;
            z += q.Z;
        }

        return new Point3D(x / points.Count, y / points.Count, z / points.Count);
    }

    /// <summary>Position key for welding flat-shaded duplicates.</summary>
    private readonly record struct WeldKey(long X, long Y, long Z)
    {
        public static WeldKey Of(Point3D p) => new((long)Math.Round(p.X * 1e9), (long)Math.Round(p.Y * 1e9), (long)Math.Round(p.Z * 1e9));
    }

    /// <summary>A logo slot whose next opacity change throws: injects a fault into the overlay's frame rendering.</summary>
    private sealed class FaultyLogo : FrameworkElement
    {
        private bool _fail;

        public void FailNextOpacityChange() => _fail = true;

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (_fail && e.Property == OpacityProperty)
            {
                _fail = false;
                throw new InvalidOperationException("simulated render fault");
            }
        }
    }

    /// <summary>A minimal shell: title bar with the logo slot, a few cards, and the intro on top.</summary>
    private sealed record Stage(Grid Host, IntroOverlay Overlay, DiamondMark Logo)
    {
        public static Stage Create()
        {
            var logo = new DiamondMark { Width = 26, Height = 26, Margin = new Thickness(14, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            shell.RowDefinitions.Add(new RowDefinition());
            var bar = new Border();
            bar.SetResourceReference(Border.BackgroundProperty, "B.Surface");
            bar.Child = new TextBlock { Text = "Carbonado", Margin = new Thickness(50, 12, 0, 0), FontSize = 14 };
            ((TextBlock)bar.Child).SetResourceReference(TextBlock.ForegroundProperty, "B.TextSecondary");
            shell.Children.Add(bar);
            shell.Children.Add(logo);
            var cards = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 3, Margin = new Thickness(32) };
            for (int i = 0; i < 6; i++)
            {
                var card = new Border { Margin = new Thickness(10), CornerRadius = new CornerRadius(16) };
                card.SetResourceReference(Border.BackgroundProperty, "B.Raised");
                cards.Children.Add(card);
            }

            Grid.SetRow(cards, 1);
            shell.Children.Add(cards);

            var overlay = new IntroOverlay { Mode = IntroMode.Cinematic, LogoTarget = logo, Tagline = TaglineText };
            var host = new Grid();
            host.Children.Add(shell);
            host.Children.Add(overlay);
            return new Stage(host, overlay, logo);
        }
    }
}
