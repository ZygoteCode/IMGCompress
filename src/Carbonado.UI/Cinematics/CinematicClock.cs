using System.Windows.Media;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Cinematics;

/// <summary>
/// Plays a <see cref="Timeline"/> in step with WPF frames (<see cref="CompositionTarget.Rendering"/>). Each frame
/// advances <see cref="Time"/> by the frame delta × <see cref="Speed"/> × <see cref="MotionSettings.Speed"/> and
/// evaluates the timeline. Reaching the end, or <see cref="Skip"/>, evaluates the final state, stops the clock and
/// raises <see cref="Completed"/> once. A run is fail-safe: if evaluating the timeline throws (in <see cref="Play"/>,
/// in a frame or in <see cref="Skip"/>), the run ends first — detached from frames, <see cref="Completed"/> raised so
/// listeners can get out of the way — and the exception then propagates (from a frame: to the dispatcher).
/// Single-threaded: use it on the dispatcher thread that owns the visuals.
/// </summary>
public sealed class CinematicClock
{
    /// <summary>
    /// Longest wall-clock step applied in one frame. A UI-thread hitch (startup work, first-chance JIT) delays the
    /// choreography instead of skipping through it.
    /// </summary>
    public static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private readonly Timeline _timeline;
    private TimeSpan? _lastFrame;
    private double _speed = 1;
    private bool _completed;

    /// <param name="timeline">Choreography to play; its <see cref="Timeline.Duration"/> is read on every frame.</param>
    public CinematicClock(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        _timeline = timeline;
    }

    /// <summary>Raised when playback reaches the end or is skipped (once per run).</summary>
    public event EventHandler? Completed;

    /// <summary>Current position in seconds, within [0, <see cref="Timeline.Duration"/>].</summary>
    public double Time { get; private set; }

    /// <summary>Local playback rate (1 = real time), multiplied by <see cref="MotionSettings.Speed"/>.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Speed must be a positive number.");
            }

            _speed = value;
        }
    }

    /// <summary>True between <see cref="Play"/> and completion (or <see cref="Pause"/>).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts or resumes playback from <see cref="Time"/> (from 0 again after a completed run) and evaluates the
    /// current state immediately. A zero-length timeline completes synchronously.
    /// </summary>
    public void Play()
    {
        if (IsRunning)
        {
            return;
        }

        if (_completed || Time >= _timeline.Duration)
        {
            Time = 0;
            _timeline.Reset();
        }

        _completed = false;
        _lastFrame = null;

        // The first frame is evaluated before subscribing to frames: if it faults, nothing stays attached.
        EvaluateOrFinish(Time);
        if (Time >= _timeline.Duration)
        {
            Finish();
            return;
        }

        IsRunning = true;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Stops advancing (keeps <see cref="Time"/>); <see cref="Play"/> resumes.</summary>
    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// Jumps to the final state and completes immediately (no-op when the run already completed). The run completes
    /// even when evaluating the final state throws; the exception then propagates.
    /// </summary>
    public void Skip()
    {
        if (_completed)
        {
            return;
        }

        Time = _timeline.Duration;
        try
        {
            _timeline.Evaluate(Time);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>
    /// Moves to <paramref name="t"/> (clamped to the timeline) and evaluates that state without completing; a running
    /// clock continues from there (and, like any frame, ends its run if the evaluation throws).
    /// </summary>
    public void Seek(double t)
    {
        if (double.IsNaN(t))
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "Time must be a number.");
        }

        Time = Math.Clamp(t, 0, _timeline.Duration);
        if (Time < _timeline.Duration)
        {
            _completed = false;
        }

        if (IsRunning)
        {
            EvaluateOrFinish(Time);
        }
        else
        {
            _timeline.Evaluate(Time);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args)
        {
            return;
        }

        if (_lastFrame is not { } last)
        {
            _lastFrame = args.RenderingTime;
            return;
        }

        // Rendering may be raised several times for the same frame.
        if (args.RenderingTime <= last)
        {
            return;
        }

        TimeSpan step = args.RenderingTime - last;
        _lastFrame = args.RenderingTime;
        if (step > MaxFrameStep)
        {
            step = MaxFrameStep;
        }

        double duration = _timeline.Duration;
        Time = Math.Min(duration, Time + (step.TotalSeconds * _speed * MotionSettings.Speed));
        EvaluateOrFinish(Time);
        if (Time >= duration)
        {
            Finish();
        }
    }

    /// <summary>
    /// Evaluates the timeline at <paramref name="t"/>; if that throws, the run ends first (detached from frames,
    /// <see cref="Completed"/> raised) and the exception is rethrown — a faulted frame never repeats every frame.
    /// </summary>
    private void EvaluateOrFinish(double t)
    {
        try
        {
            _timeline.Evaluate(t);
        }
        catch
        {
            Finish();
            throw;
        }
    }

    /// <summary>Ends the run: detaches from frames and raises <see cref="Completed"/> once (re-entrant safe).</summary>
    private void Finish()
    {
        Pause();
        if (_completed)
        {
            return;
        }

        _completed = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
