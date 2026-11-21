using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;
using Carbonado.UI.Tests.Controls;
using Carbonado.UI.Tests.TestSupport;
using MotionVsm = Carbonado.UI.Theme.MotionVisualStateManager;

namespace Carbonado.UI.Tests.Theme;

/// <summary>
/// The motion-aware state manager every Carbonado template uses: under reduced motion finite state storyboards land
/// on their final frame at once, looping ones park on their first frame, and generated transitions are skipped.
/// </summary>
[Collection(MotionSettingsCollection.Name)]
public class MotionStateManagerTests
{
    // A probe template: one finite (1 s), one looping (1 s, forever) and one instant state reached through a 1 s transition.
    private const string ProbeTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:theme="clr-namespace:Carbonado.UI.Theme;assembly=Carbonado.UI"
                         TargetType="Control">
            <Grid x:Name="Root">
                <VisualStateManager.CustomVisualStateManager>
                    <theme:MotionVisualStateManager />
                </VisualStateManager.CustomVisualStateManager>
                <VisualStateManager.VisualStateGroups>
                    <VisualStateGroup x:Name="Probe">
                        <VisualStateGroup.Transitions>
                            <VisualTransition To="Glide" GeneratedDuration="0:0:1" />
                        </VisualStateGroup.Transitions>
                        <VisualState x:Name="Rest" />
                        <VisualState x:Name="Finite">
                            <Storyboard>
                                <DoubleAnimation Storyboard.TargetName="A" Storyboard.TargetProperty="Opacity" To="0.2" Duration="0:0:1" />
                            </Storyboard>
                        </VisualState>
                        <VisualState x:Name="Looping">
                            <Storyboard RepeatBehavior="Forever">
                                <DoubleAnimation Storyboard.TargetName="B" Storyboard.TargetProperty="Opacity" From="0" To="1" Duration="0:0:1" />
                            </Storyboard>
                        </VisualState>
                        <VisualState x:Name="Glide">
                            <Storyboard>
                                <DoubleAnimation Storyboard.TargetName="C" Storyboard.TargetProperty="Opacity" To="0.6" Duration="0" />
                            </Storyboard>
                        </VisualState>
                    </VisualStateGroup>
                </VisualStateManager.VisualStateGroups>
                <Border x:Name="A" />
                <Border x:Name="B" Opacity="0.5" />
                <Border x:Name="C" />
            </Grid>
        </ControlTemplate>
        """;

    [Fact]
    public void ReducedMotion_FiniteSkipsToEnd_LoopingParks_TransitionsSkipped() => Sta.RunLoop(async () =>
    {
        MotionSettings.SetReducedMotion(true);
        try
        {
            var (live, probe, a, b, c) = await ShowProbe();
            using var window = live;

            Assert.True(VisualStateManager.GoToState(probe, "Finite", true));
            await Task.Delay(120);
            Assert.Equal(0.2, a.Opacity, 3); // a 1 s animation would still be near 0.9

            Assert.True(VisualStateManager.GoToState(probe, "Glide", true));
            await Task.Delay(120);
            Assert.Equal(0.6, c.Opacity, 3); // no 1 s generated transition

            Assert.True(VisualStateManager.GoToState(probe, "Looping", true));
            await Task.Delay(120);
            double first = b.Opacity;
            await Task.Delay(250);
            Assert.Equal(0, first, 3); // parked on its first frame (From = 0)
            Assert.Equal(first, b.Opacity, 3); // and not moving
        }
        finally
        {
            MotionSettings.SetReducedMotion(null);
        }
    });

    [Fact]
    public void FullMotion_StoryboardsAndTransitionsPlay() => Sta.RunLoop(async () =>
    {
        MotionSettings.SetReducedMotion(false);
        try
        {
            var (live, probe, a, b, c) = await ShowProbe();
            using var window = live;

            Assert.True(VisualStateManager.GoToState(probe, "Finite", true));
            await Task.Delay(120);
            Assert.True(a.Opacity > 0.5, $"the finite storyboard should still be running (opacity {a.Opacity:F3})");
            await LiveWindow.Until(() => Math.Abs(a.Opacity - 0.2) < 0.001, TimeSpan.FromSeconds(3));

            Assert.True(VisualStateManager.GoToState(probe, "Looping", true));
            await Task.Delay(150);
            double first = b.Opacity;
            await Task.Delay(250);
            Assert.NotEqual(first, b.Opacity, 3);

            Assert.True(VisualStateManager.GoToState(probe, "Glide", true));
            await Task.Delay(120);
            Assert.True(c.Opacity > 0.75, $"the 1 s generated transition should still be easing (opacity {c.Opacity:F3})");
        }
        finally
        {
            MotionSettings.SetReducedMotion(null);
        }
    });

    [Fact]
    public void IsInfinite_FindsForeverAtAnyDepth()
    {
        var finite = new Storyboard { Children = { new DoubleAnimation(1, TimeSpan.FromSeconds(1)) } };
        var nested = new Storyboard
        {
            Children = { new ParallelTimeline { Children = { new DoubleAnimation(1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } } } },
        };
        Assert.False(MotionVsm.IsInfinite(finite));
        Assert.True(MotionVsm.IsInfinite(new Storyboard { RepeatBehavior = RepeatBehavior.Forever }));
        Assert.True(MotionVsm.IsInfinite(nested));
        Assert.False(MotionVsm.IsInfinite(new Storyboard { RepeatBehavior = new RepeatBehavior(3) }));
    }

    private static async Task<(LiveWindow Live, Control Probe, Border A, Border B, Border C)> ShowProbe()
    {
        var probe = new Control { Template = (ControlTemplate)XamlReader.Parse(ProbeTemplate), Width = 40, Height = 40 };
        var live = new LiveWindow(probe, 120, 120);
        await LiveWindow.Until(() => probe.IsLoaded);
        Border Part(string name) => (Border)probe.Template.FindName(name, probe);
        return (live, probe, Part("A"), Part("B"), Part("C"));
    }
}
