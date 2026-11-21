using Carbonado.UI.Motion;

namespace Carbonado.UI.Cinematics;

/// <summary>
/// The Carbonado motion vocabulary ("Pressure &amp; Refraction") as <see cref="Easing"/> curves. All curves are pure,
/// allocation-free after construction and clamp their input to [0, 1].
/// </summary>
public static class Easings
{
    /// <summary>Identity.</summary>
    public static readonly Easing Linear = t => Clamp01(t);

    /// <summary>cubic-bezier(0.16, 1, 0.3, 1): fast start, long luminous settle (entrances, reveals).</summary>
    public static readonly Easing Crystallize = Bezier(0.16, 1, 0.3, 1);

    /// <summary>cubic-bezier(0.7, 0, 0.84, 0): pressure building, slow then violent (implosions, exits).</summary>
    public static readonly Easing Implode = Bezier(0.7, 0, 0.84, 0);

    /// <summary>cubic-bezier(0.4, 0, 0.2, 1): smooth travel (shared-element flights, dissolves).</summary>
    public static readonly Easing Glide = Bezier(0.4, 0, 0.2, 1);

    /// <summary>Back-out with overshoot 1.15 (≈ 4.9 % past the target before settling).</summary>
    public static readonly Easing Settle = BackOut(1.15);

    /// <summary>Exponential ease-out, 1 − 2^(−10t).</summary>
    public static readonly Easing ExpoOut = t => t >= 1 ? 1 : t <= 0 ? 0 : 1 - Math.Pow(2, -10 * t);

    /// <summary>Exponential ease-in, 2^(10t − 10).</summary>
    public static readonly Easing ExpoIn = t => t <= 0 ? 0 : t >= 1 ? 1 : Math.Pow(2, (10 * t) - 10);

    /// <summary>Sinusoidal ease-in-out.</summary>
    public static readonly Easing SineInOut = t => (1 - Math.Cos(Math.PI * Clamp01(t))) / 2;

    /// <summary>Elastic ease-out: overshoots and rings down within the duration.</summary>
    public static readonly Easing ElasticOut = t => t <= 0 ? 0 : t >= 1 ? 1
        : (Math.Pow(2, -10 * t) * Math.Sin(((t * 10) - 0.75) * (2 * Math.PI / 3))) + 1;

    /// <summary>
    /// CSS-style cubic-bezier curve from (0,0) to (1,1) with control points (x1,y1), (x2,y2). Solved with Newton
    /// iterations and a bisection fallback by <see cref="CubicBezier"/> (error ≤ 1e-6).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">x1 or x2 outside [0, 1].</exception>
    public static Easing Bezier(double x1, double y1, double x2, double y2)
    {
        var curve = new CubicBezier(x1, y1, x2, y2);
        return curve.Evaluate;
    }

    /// <summary>Back-out curve 1 + (s + 1)(t − 1)³ + s(t − 1)² with overshoot parameter <paramref name="overshoot"/>.</summary>
    public static Easing BackOut(double overshoot) => t =>
    {
        double u = Clamp01(t) - 1;
        return 1 + ((overshoot + 1) * u * u * u) + (overshoot * u * u);
    };

    private static double Clamp01(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t;
}
