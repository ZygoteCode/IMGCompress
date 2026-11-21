using System.Diagnostics;
using System.Windows.Media;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Controls;

/// <summary>
/// Per-frame tween for custom-drawn controls (counters, gauges, charts): calls <c>apply</c> with eased progress
/// 0 → 1 on every <see cref="CompositionTarget.Rendering"/> until done, then unsubscribes (no idle frames).
/// Durations go through <see cref="MotionSettings.Scale(TimeSpan)"/>; under reduced motion the tween jumps to
/// the end when <c>skipWhenReduced</c> is set.
/// </summary>
internal sealed class FrameTween
{
    private readonly Action<double> _apply;
    private readonly Stopwatch _clock = new();
    private CubicBezier _ease;
    private TimeSpan _duration;
    private bool _subscribed;

    public FrameTween(Action<double> apply)
    {
        _apply = apply;
    }

    /// <summary>True while frames are being delivered.</summary>
    public bool IsRunning => _subscribed;

    /// <summary>Starts (or restarts) the tween; progress 1 is applied immediately when there is nothing to animate.</summary>
    public void Start(TimeSpan duration, CubicBezier ease, bool skipWhenReduced = true)
    {
        _ease = ease;
        _duration = MotionSettings.Scale(duration);
        if (_duration <= TimeSpan.Zero || (skipWhenReduced && MotionSettings.ReducedMotion))
        {
            Complete();
            return;
        }

        _clock.Restart();
        if (!_subscribed)
        {
            _subscribed = true;
            CompositionTarget.Rendering += OnRendering;
        }

        _apply(0);
    }

    /// <summary>Jumps to the end (progress 1) and stops.</summary>
    public void Complete()
    {
        Stop();
        _apply(1);
    }

    /// <summary>Stops delivering frames, leaving the last applied progress.</summary>
    public void Stop()
    {
        if (_subscribed)
        {
            _subscribed = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        _clock.Reset();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds;
        if (t >= 1)
        {
            Complete();
            return;
        }

        _apply(_ease.Evaluate(t));
    }
}
