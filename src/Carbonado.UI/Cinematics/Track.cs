namespace Carbonado.UI.Cinematics;

/// <summary>
/// Keyframed value over time. <see cref="Evaluate"/> is a pure function of t: before the first key it holds the
/// first value, after the last key the last value, and in between it interpolates the surrounding keys with the
/// later key's easing. Keys with equal times form an instantaneous step (the later-added key wins from that time on).
/// </summary>
/// <typeparam name="T">Animated value type.</typeparam>
public sealed class Track<T>
{
    private readonly Func<T, T, double, T> _lerp;
    private readonly List<Keyframe<T>> _keys = [];

    /// <param name="lerp">Interpolates (from, to, eased progress); progress may leave [0, 1] for overshooting curves.</param>
    public Track(Func<T, T, double, T> lerp)
    {
        ArgumentNullException.ThrowIfNull(lerp);
        _lerp = lerp;
    }

    /// <summary>Keys sorted by time.</summary>
    public IReadOnlyList<Keyframe<T>> Keys => _keys;

    /// <summary>Time of the first key (0 when empty).</summary>
    public double Start => _keys.Count == 0 ? 0 : _keys[0].Time;

    /// <summary>Time of the last key (0 when empty).</summary>
    public double End => _keys.Count == 0 ? 0 : _keys[^1].Time;

    /// <summary>Adds a key; <paramref name="ease"/> (default linear) shapes the segment ending at this key.</summary>
    /// <returns>This track, for chaining.</returns>
    public Track<T> Key(double time, T value, Easing? ease = null)
    {
        if (!double.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "Key time must be finite.");
        }

        int index = _keys.Count;
        while (index > 0 && _keys[index - 1].Time > time)
        {
            index--;
        }

        _keys.Insert(index, new Keyframe<T>(time, value, ease ?? Easings.Linear));
        return this;
    }

    /// <summary>Value of the track at time <paramref name="t"/>.</summary>
    /// <exception cref="InvalidOperationException">The track has no keys.</exception>
    public T Evaluate(double t)
    {
        if (_keys.Count == 0)
        {
            throw new InvalidOperationException("The track has no keys.");
        }

        // Last key with Time <= t (binary search); -1 when t precedes every key.
        int lo = 0, hi = _keys.Count - 1, index = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_keys[mid].Time <= t)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (index < 0)
        {
            return _keys[0].Value;
        }

        if (index == _keys.Count - 1)
        {
            return _keys[index].Value;
        }

        Keyframe<T> from = _keys[index], to = _keys[index + 1];
        double progress = (t - from.Time) / (to.Time - from.Time);
        return _lerp(from.Value, to.Value, to.Ease(progress));
    }
}

/// <summary>Factories for common <see cref="Track{T}"/> value types.</summary>
public static class Track
{
    /// <summary>A track of doubles with linear interpolation (overshoot-friendly).</summary>
    public static Track<double> Double() => new(static (a, b, t) => a + ((b - a) * t));
}
