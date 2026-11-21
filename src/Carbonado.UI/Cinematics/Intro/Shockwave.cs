using System.Windows;
using System.Windows.Media;
using Carbonado.UI.Brand;
using Carbonado.UI.Effects;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The ignition: the core heating under pressure, the white-hot flash, and the shockwave — a prismatic ring
/// (aqua inside, white-hot crest, rose outside) whose front also refracts the scene beneath it through
/// <see cref="RippleEffect"/>.
/// </summary>
internal sealed class Shockwave : IntroLayer
{
    private readonly DrawingVisual _visual;
    private readonly UIElement _refracted;
    private RippleEffect? _ripple;

    /// <param name="refracted">Element whose content the shock front distorts.</param>
    public Shockwave(UIElement refracted)
    {
        _refracted = refracted;
        _visual = Add(new DrawingVisual());
    }

    /// <summary>Shock front radius in design units for progress <paramref name="front"/>.</summary>
    private static double FrontRadius(double front, double reach) => 8 + (reach * 1.08 * front);

    public override void Render(IntroState state, in IntroLayout layout)
    {
        _visual.Transform = layout.DesignTransform;
        using (DrawingContext dc = _visual.RenderOpen())
        {
            if (!layout.IsEmpty)
            {
                BrandPalette p = BrandPalette.Current;
                DrawCore(dc, p, state);
                DrawFlash(dc, p, state, layout);
                DrawFront(dc, p, state, layout.Reach);
            }
        }

        if (UseEffects && state.Ripple > 0.001 && state.Shockwave > 0 && !layout.IsEmpty)
        {
            _ripple ??= new RippleEffect { Thickness = 0.05 };
            _ripple.Center = layout.RelativeCore;
            _ripple.Radius = FrontRadius(state.Shockwave, layout.Reach) * layout.Scale / layout.Width;
            _ripple.Amplitude = 0.02 * state.Ripple;
            _ripple.AspectRatio = layout.Width / layout.Height;
            _ripple.Highlight = 0.45 * state.Ripple;
            _refracted.Effect = _ripple;
        }
        else
        {
            _refracted.Effect = null;
        }
    }

    private static void DrawCore(DrawingContext dc, BrandPalette p, IntroState state)
    {
        double heat = state.CoreHeat;
        if (heat <= 0.002)
        {
            return;
        }

        double radius = 9 + (120 * heat * heat);
        dc.DrawEllipse(Glow(p, heat), null, default, radius, radius);
    }

    private static void DrawFlash(DrawingContext dc, BrandPalette p, IntroState state, in IntroLayout layout)
    {
        double flash = state.Flash;
        if (flash <= 0.002)
        {
            return;
        }

        // Screen wash, then the expanding white-hot bloom riding just behind the shock front.
        double halfW = layout.Width / 2 / layout.Scale, halfH = layout.Height / layout.Scale;
        var wash = new SolidColorBrush(BrandPalette.WithAlpha(BrandPalette.Mix(p.Diamond50, p.Ice, 0.25), 0.07 * flash));
        wash.Freeze();
        dc.DrawRectangle(wash, null, new Rect(-halfW - 40, -halfH - 40, (2 * halfW) + 80, (2 * halfH) + 80));

        double radius = 90 + (0.85 * FrontRadius(state.Shockwave, layout.Reach));
        dc.DrawEllipse(Glow(p, 0.9 * flash), null, default, radius, radius);
    }

    private static void DrawFront(DrawingContext dc, BrandPalette p, IntroState state, double reach)
    {
        double front = state.Shockwave;
        if (front <= 0 || front >= 1)
        {
            return;
        }

        double r = FrontRadius(front, reach);
        double width = 2 + (24 * Math.Pow(1 - front, 1.4));
        double alpha = Math.Pow(1 - front, 1.15);
        Ring(dc, p.Iris, alpha * 0.2, r, width * 3.2);
        Ring(dc, p.Rose, alpha * 0.55, r + (width * 0.45), width * 0.32);
        Ring(dc, p.Aqua, alpha * 0.7, r - (width * 0.45), width * 0.32);
        Ring(dc, p.Diamond50, alpha * 0.95, r, width * 0.4);
    }

    private static void Ring(DrawingContext dc, Color color, double alpha, double radius, double width)
    {
        if (radius <= 0 || alpha <= 0.002)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(color, alpha)), width);
        pen.Freeze();
        dc.DrawEllipse(null, pen, default, radius, radius);
    }

    /// <summary>White-hot radial bloom: diamond white → ice → aqua → iris → nothing.</summary>
    private static Brush Glow(BrandPalette p, double intensity)
    {
        var brush = new RadialGradientBrush(
            [
                new GradientStop(BrandPalette.WithAlpha(p.Diamond50, intensity), 0),
                new GradientStop(BrandPalette.WithAlpha(p.Ice, 0.8 * intensity), 0.14),
                new GradientStop(BrandPalette.WithAlpha(p.Aqua, 0.4 * intensity), 0.38),
                new GradientStop(BrandPalette.WithAlpha(p.Iris, 0.14 * intensity), 0.68),
                new GradientStop(p.Transparent, 1),
            ]);
        brush.Freeze();
        return brush;
    }
}
