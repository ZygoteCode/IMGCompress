using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Carbonado.UI.Effects;
using Carbonado.UI.Motion;
using Carbonado.UI.Tests.Controls;
using Carbonado.UI.Tests.TestSupport;
using Carbonado.UI.Tests.Theme;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Tests.Transitions;

[Collection(MotionSettingsCollection.Name)]
public class TransitionTests
{
    [Fact]
    public void Shaders_LoadFromPackUri() => Sta.Run(() =>
    {
        Snapshot.EnsurePackScheme();
        foreach (string file in new[] { "ChromaticAberration.ps", "Ripple.ps", "Grain.ps" })
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Carbonado.UI;component/Effects/{file}"));
            Assert.NotNull(info);
            using var stream = info.Stream;
            Assert.True(stream.Length > 64, $"{file} is empty");
        }

        foreach (ShaderEffect effect in new ShaderEffect[] { new ChromaticAberrationEffect(), new RippleEffect(), new GrainEffect() })
        {
            var shader = (PixelShader)typeof(ShaderEffect).GetProperty("PixelShader", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(effect)!;
            Assert.True(shader.IsFrozen);
        }

        _ = EffectSupport.IsHardwareAccelerated; // queries the render tier without throwing
    });

    [Fact]
    public void TransitionHost_SwapsContent_AndEndsWithOnlyNewContent() => Sta.RunLoop(async () =>
    {
        MotionSettings.SetReducedMotion(false);
        try
        {
            var outgoing = Page("Forge", "Border.Card");
            var incoming = Page("Compare", "Border.Glass");
            var host = new TransitionHost { Content = outgoing, Width = 440, Height = 260 };
            using var live = new LiveWindow(host, 480, 300);
            await LiveWindow.Until(() => host.IsLoaded && outgoing.IsVisible);

            host.Content = incoming;
            var plan = Assert.IsType<TransitionPlan>(host.LastPlan);
            Assert.Equal(TransitionKind.Refraction, plan.Kind);
            Assert.Equal(TransitionHost.OutDrift, plan.Drift);
            Assert.Equal(TransitionHost.InRise, plan.Rise);
            Assert.Equal(TimeSpan.FromMilliseconds(180), plan.OutDuration);
            Assert.Equal(TimeSpan.FromMilliseconds(420), plan.InDuration);
            Assert.Equal(EffectSupport.IsHardwareAccelerated, plan.Aberration);
            Assert.True(host.IsTransitioning);
            Assert.Equal(Visibility.Visible, host.SnapshotPart!.Visibility);
            Assert.NotNull(host.SnapshotPart.Source);

            // A frame from the middle of the swap, for visual review.
            await Task.Delay(110);
            SaveFrame(host, "refraction-mid");

            await LiveWindow.Until(() => !host.IsTransitioning, TimeSpan.FromSeconds(3));
            var presenter = host.ContentPart!;
            Assert.Same(incoming, presenter.Content);
            Assert.Equal(Visibility.Collapsed, host.SnapshotPart.Visibility);
            Assert.Null(host.SnapshotPart.Source);
            Assert.Null(host.SnapshotPart.Effect);
            Assert.Null(presenter.Effect);
            Assert.True(presenter.RenderTransform.Value.IsIdentity);
            Assert.Equal(1.0, presenter.Opacity);
            Assert.False(IsInTree(host, outgoing), "the outgoing page is still in the visual tree");
            Assert.True(incoming.IsVisible);
            SaveFrame(host, "refraction-end");
        }
        finally
        {
            MotionSettings.SetReducedMotion(null);
        }
    });

    [Fact]
    public void TransitionHost_ReducedMotion_IsFadeOnly() => Sta.RunLoop(async () =>
    {
        MotionSettings.SetReducedMotion(true);
        try
        {
            var incoming = Page("Archive", "Border.Card");
            var card = Assert.IsAssignableFrom<Panel>(((Border)incoming).Child).Children.OfType<FrameworkElement>().First();
            var host = new TransitionHost { Content = Page("Forge", "Border.Card"), Width = 440, Height = 260 };
            using var live = new LiveWindow(host, 480, 300);
            await LiveWindow.Until(() => host.IsLoaded);

            host.Content = incoming;
            var plan = Assert.IsType<TransitionPlan>(host.LastPlan);
            Assert.True(plan.IsFadeOnly);
            Assert.True(plan.InDuration <= TimeSpan.FromMilliseconds(120));
            Assert.True(plan.OutDuration <= TimeSpan.FromMilliseconds(120));

            // Mid-way: opacity only — no drift, rise, scale, blur or dispersion anywhere.
            await Task.Delay(40);
            Assert.True(host.ContentPart!.RenderTransform.Value.IsIdentity);
            Assert.Null(host.ContentPart.Effect);
            Assert.True(host.SnapshotPart!.RenderTransform.Value.IsIdentity);
            Assert.Null(host.SnapshotPart.Effect);
            Assert.True(card.RenderTransform.Value.IsIdentity, "staggered children must not move under reduced motion");

            await LiveWindow.Until(() => !host.IsTransitioning, TimeSpan.FromSeconds(1));
            Assert.Same(incoming, host.ContentPart.Content);
        }
        finally
        {
            MotionSettings.SetReducedMotion(null);
        }
    });

    [Fact]
    public void TransitionHost_None_SwapsInstantly() => Sta.RunLoop(async () =>
    {
        var host = new TransitionHost { Content = Page("Forge", "Border.Card"), Transition = TransitionKind.None };
        using var live = new LiveWindow(host);
        await LiveWindow.Until(() => host.IsLoaded);
        var next = Page("Engines", "Border.Card");
        host.Content = next;
        Assert.Null(host.LastPlan);
        Assert.False(host.IsTransitioning);
        Assert.Equal(Visibility.Collapsed, host.SnapshotPart!.Visibility);
    });

    [Fact]
    public void Stagger_OrdersDelays() => Sta.Run(() =>
    {
        var first = new Border();
        var second = new Border();
        var third = new Border();
        var loose = new Border();
        Stagger.SetIndex(third, 2);
        Stagger.SetIndex(first, 0);
        Stagger.SetIndex(second, 1);
        var nested = new StackPanel { Children = { second } };
        var panel = new StackPanel { Children = { third, loose, nested, first } };

        var plan = Stagger.Plan(panel);
        Assert.Equal([first, second, third], plan.Select(i => i.Element).ToArray());
        Assert.Equal([0, 35, 70], plan.Select(i => (int)i.Delay.TotalMilliseconds).ToArray());

        // Step is inherited: a container sets the rhythm for everything inside it.
        Stagger.SetStep(panel, TimeSpan.FromMilliseconds(50));
        Assert.Equal([0, 50, 100], Stagger.Plan(panel).Select(i => (int)i.Delay.TotalMilliseconds).ToArray());
        Assert.Equal(-1, Stagger.GetIndex(loose));
        Assert.Throws<ArgumentException>(() => Stagger.SetStep(panel, TimeSpan.FromMilliseconds(-1)));
    });

    /// <summary>A page-like card: a title, a body line and a row of staggered chips.</summary>
    private static FrameworkElement Page(string title, string surface)
    {
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, Style = (Style)ThemeHarness.Theme["Text.Display"] };
        Stagger.SetIndex(heading, 0);
        stack.Children.Add(heading);
        for (int i = 1; i <= 3; i++)
        {
            var line = new TextBlock { Text = $"Staggered line {i}", Style = (Style)ThemeHarness.Theme["Text.Body"] };
            Stagger.SetIndex(line, i);
            stack.Children.Add(line);
        }

        return new Border { Style = (Style)ThemeHarness.Theme[surface], Child = stack, Margin = new Thickness(16) };
    }

    private static bool IsInTree(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (IsInTree(VisualTreeHelper.GetChild(root, i), target))
            {
                return true;
            }
        }

        return false;
    }

    private static void SaveFrame(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)ThemeHarness.Theme["B.Window"], null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        bitmap.Render(visual);
        ThemeHarness.Save(bitmap, Path.Combine(Snapshot.ArtifactsDir("ui/transitions"), name + ".png"));
    }
}
