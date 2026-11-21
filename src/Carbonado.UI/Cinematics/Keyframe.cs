namespace Carbonado.UI.Cinematics;

/// <summary>
/// One key of a <see cref="Track{T}"/>: at <paramref name="Time"/> the track holds <paramref name="Value"/>;
/// <paramref name="Ease"/> shapes the segment that ends at this key (WPF key-frame convention).
/// </summary>
/// <typeparam name="T">Animated value type.</typeparam>
/// <param name="Time">Key time in seconds.</param>
/// <param name="Value">Value reached at <paramref name="Time"/>.</param>
/// <param name="Ease">Curve of the interpolation from the previous key to this one.</param>
public readonly record struct Keyframe<T>(double Time, T Value, Easing Ease);
