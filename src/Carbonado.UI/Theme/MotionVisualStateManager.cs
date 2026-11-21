using System.Windows;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Theme;

/// <summary>
/// Visual state manager attached to every Carbonado control template (<c>Motion.StateManager</c> resource).
/// Under <see cref="MotionSettings.ReducedMotion"/> state changes are instant: transitions are skipped and state
/// storyboards jump to their final frame (looping ones hold their first frame). Otherwise state storyboards run at
/// <see cref="MotionSettings.Speed"/>.
/// </summary>
public sealed class MotionVisualStateManager : VisualStateManager
{
    protected override bool GoToStateCore(FrameworkElement control, FrameworkElement stateGroupsRoot, string stateName,
        VisualStateGroup group, VisualState state, bool useTransitions)
    {
        // WPF calls custom managers even for states the template does not define (group/state null).
        if (group is null || state is null)
        {
            return false;
        }

        bool reduced = MotionSettings.ReducedMotion;
        bool changed = base.GoToStateCore(control, stateGroupsRoot, stateName, group, state, useTransitions && !reduced);
        if (changed && state.Storyboard is { } storyboard)
        {
            if (reduced)
            {
                Settle(storyboard, stateGroupsRoot);
            }
            else if (MotionSettings.Speed != 1.0)
            {
                storyboard.SetSpeedRatio(stateGroupsRoot, MotionSettings.Speed);
            }
        }

        return changed;
    }

    /// <summary>
    /// Brings a running state storyboard to rest: finite storyboards skip to their fill values, looping ones are
    /// parked on their first frame.
    /// </summary>
    public static void Settle(Storyboard storyboard, FrameworkElement containingObject)
    {
        if (IsInfinite(storyboard))
        {
            storyboard.Seek(containingObject, TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            storyboard.Pause(containingObject);
        }
        else
        {
            storyboard.SkipToFill(containingObject);
        }
    }

    /// <summary>True when the timeline (or any child) repeats forever.</summary>
    public static bool IsInfinite(Timeline timeline)
    {
        if (timeline.RepeatBehavior == RepeatBehavior.Forever)
        {
            return true;
        }

        if (timeline is TimelineGroup group)
        {
            foreach (var child in group.Children)
            {
                if (IsInfinite(child))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
