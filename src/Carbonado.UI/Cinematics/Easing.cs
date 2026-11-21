namespace Carbonado.UI.Cinematics;

/// <summary>
/// Timing curve: maps linear progress <paramref name="t"/> in [0, 1] to eased progress. Every curve returns exactly
/// 0 at t ≤ 0 and 1 at t ≥ 1; values in between may overshoot (back, elastic).
/// </summary>
/// <param name="t">Linear progress; values outside [0, 1] are clamped.</param>
public delegate double Easing(double t);
