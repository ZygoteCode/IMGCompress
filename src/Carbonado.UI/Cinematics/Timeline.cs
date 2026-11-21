namespace Carbonado.UI.Cinematics;

/// <summary>
/// Deterministic choreography: tracks bound to setters plus timed cues. <see cref="Evaluate"/> applies every bound
/// track at t (in binding order), so the same t always produces the same state regardless of history; cues
/// registered with <see cref="At"/> fire once each time playback crosses them moving forward.
/// </summary>
public sealed class Timeline
{
    private readonly List<IBinding> _bindings = [];
    private readonly List<Cue> _cues = [];
    private double? _duration;
    private double _position = double.NegativeInfinity;

    private interface IBinding
    {
        double End { get; }

        void Apply(double t);
    }

    /// <summary>
    /// Length in seconds: the explicit value when set, otherwise the latest key or cue time (0 when empty).
    /// </summary>
    public double Duration
    {
        get
        {
            if (_duration is { } explicitDuration)
            {
                return explicitDuration;
            }

            double end = _cues.Count == 0 ? 0 : Math.Max(0, _cues[^1].Time);
            foreach (IBinding binding in _bindings)
            {
                end = Math.Max(end, binding.End);
            }

            return end;
        }

        set
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Duration must be finite and non-negative.");
            }

            _duration = value;
        }
    }

    /// <summary>Binds <paramref name="track"/> to <paramref name="apply"/>, which receives the track value on every evaluation.</summary>
    /// <returns>This timeline, for chaining.</returns>
    public Timeline Bind<T>(Track<T> track, Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(apply);
        _bindings.Add(new Binding<T>(track, apply));
        return this;
    }

    /// <summary>Registers <paramref name="action"/> to run when playback crosses <paramref name="time"/> moving forward.</summary>
    /// <returns>This timeline, for chaining.</returns>
    public Timeline At(double time, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Cue time must be finite.");
        }

        int index = _cues.Count;
        while (index > 0 && _cues[index - 1].Time > time)
        {
            index--;
        }

        _cues.Insert(index, new Cue(time, action));
        return this;
    }

    /// <summary>
    /// Applies the state for time <paramref name="t"/>, then fires (in time order) the cues in (previous t, t] when
    /// moving forward. Moving backwards fires nothing and re-arms the cues after <paramref name="t"/>.
    /// </summary>
    public void Evaluate(double t)
    {
        if (double.IsNaN(t))
        {
            throw new ArgumentOutOfRangeException(nameof(t), t, "Time must be a number.");
        }

        foreach (IBinding binding in _bindings)
        {
            binding.Apply(t);
        }

        double previous = _position;
        _position = t;
        if (t <= previous)
        {
            return;
        }

        foreach (Cue cue in _cues)
        {
            if (cue.Time > t)
            {
                break;
            }

            if (cue.Time > previous)
            {
                cue.Action();
            }
        }
    }

    /// <summary>Re-arms every cue, as if playback had never started (the next evaluation fires cues at t ≤ it).</summary>
    public void Reset() => _position = double.NegativeInfinity;

    private readonly record struct Cue(double Time, Action Action);

    private sealed class Binding<T>(Track<T> track, Action<T> apply) : IBinding
    {
        public double End => track.End;

        public void Apply(double t) => apply(track.Evaluate(t));
    }
}
