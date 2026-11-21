using System.Windows;

namespace Carbonado.UI.Motion;

/// <summary>
/// Global motion switches read by every animation in Carbonado. Reduced motion collapses transforms to short
/// fades and shortens the intro; <see cref="Speed"/> scales all durations (tests use large values).
/// </summary>
public static class MotionSettings
{
    private static bool? _reducedMotionOverride;
    private static double _speed = 1.0;

    public static event EventHandler? Changed;

    /// <summary>True when the user asked for reduced motion (app setting) or Windows animations are disabled.</summary>
    public static bool ReducedMotion => _reducedMotionOverride ?? !SystemAnimationsEnabled;

    /// <summary>Windows "Show animations" setting.</summary>
    public static bool SystemAnimationsEnabled
    {
        get
        {
            try
            {
                return SystemParameters.ClientAreaAnimation;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Playback speed multiplier (1 = normal). Must be &gt; 0.</summary>
    public static double Speed
    {
        get => _speed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _speed = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Sets the app-level preference; null follows Windows.</summary>
    public static void SetReducedMotion(bool? value)
    {
        _reducedMotionOverride = value;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Scales a duration by <see cref="Speed"/>; under reduced motion caps it at 120 ms.</summary>
    public static TimeSpan Scale(TimeSpan duration)
    {
        var scaled = TimeSpan.FromTicks((long)(duration.Ticks / _speed));
        return ReducedMotion && scaled > TimeSpan.FromMilliseconds(120) ? TimeSpan.FromMilliseconds(120) : scaled;
    }

    public static Duration Scale(Duration duration) =>
        duration.HasTimeSpan ? new Duration(Scale(duration.TimeSpan)) : duration;
}
