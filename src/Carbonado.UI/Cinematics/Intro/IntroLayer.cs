using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// A full-overlay, non-interactive layer of the intro that draws into its own visuals. <see cref="Render"/> must
/// be a pure function of the state and layout: no accumulated simulation, so any frame can be rendered directly.
/// </summary>
internal abstract class IntroLayer : FrameworkElement
{
    private readonly VisualCollection _visuals;

    protected IntroLayer()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Pixel-shader effects allowed (hardware tier; off in the software fallback).</summary>
    public bool UseEffects { get; set; } = true;

    protected override int VisualChildrenCount => _visuals.Count;

    /// <summary>Applies the frame described by <paramref name="state"/> at the given layout.</summary>
    public abstract void Render(IntroState state, in IntroLayout layout);

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected T Add<T>(T visual)
        where T : Visual
    {
        _visuals.Add(visual);
        return visual;
    }

    /// <summary>Deterministic hash of (index, salt) to [0, 1) — the seed of every particle.</summary>
    protected static double Hash01(int index, int salt)
    {
        uint h = unchecked(((uint)index * 0x9E3779B1u) ^ ((uint)salt * 0x85EBCA77u) ^ 0x27D4EB2Fu);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        h = unchecked(h * 0x297A2D39u);
        h ^= h >> 15;
        return h / 4294967296.0;
    }

    protected static double Clamp01(double value) => value <= 0 ? 0 : value >= 1 ? 1 : value;

    protected static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3 - (2 * t));
    }

    protected static Point Polar(double radius, double angle) => new(radius * Math.Cos(angle), radius * Math.Sin(angle));
}
