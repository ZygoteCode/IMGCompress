using System.Windows;
using System.Windows.Media;
using Carbonado.UI.Brand;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Light leaving the core: the 24 god rays of the ignition (white-hot at the root, fading to iris) and, during the
/// crystallization, the spectral dispersion fans — white light split into the brand spectrum, sweeping around the
/// diamond like light through a prism.
/// </summary>
internal sealed class LightRays : IntroLayer
{
    private const int RayCount = 24;
    private const int FanWedges = 6;
    private const double WedgeDegrees = 2.6;
    private const double FanStartDegrees = 200;
    private const double FanSweepDegrees = 115;

    private readonly DrawingVisual _godRays;
    private readonly DrawingVisual _fans;
    private readonly Ray[] _rays = new Ray[RayCount];
    private Brush[] _rayBrushes = [];
    private Brush[] _fanBrushes = [];
    private double _brushReach = -1;

    public LightRays()
    {
        _fans = Add(new DrawingVisual());
        _godRays = Add(new DrawingVisual());
        for (int i = 0; i < RayCount; i++)
        {
            _rays[i] = new Ray(
                Angle: ((i + ((Hash01(i, 31) - 0.5) * 0.6)) * 2 * Math.PI) / RayCount,
                HalfWidth: (0.08 + (0.5 * Math.Pow(Hash01(i, 32), 2))) * Math.PI / 180,
                Length: 0.5 + (0.65 * Hash01(i, 33)),
                Group: (int)(Hash01(i, 34) * 3));
        }
    }

    public override void Render(IntroState state, in IntroLayout layout)
    {
        _godRays.Transform = layout.DesignTransform;
        _fans.Transform = layout.DesignTransform;
        if (!layout.IsEmpty && Math.Abs(_brushReach - layout.Reach) > 0.5)
        {
            CreateBrushes(layout.Reach);
        }

        using (DrawingContext dc = _godRays.RenderOpen())
        {
            if (!layout.IsEmpty && state.RaysAlpha > 0.003 && state.Rays > 0)
            {
                DrawGodRays(dc, state, layout.Reach);
            }
        }

        _godRays.Opacity = state.RaysAlpha;

        using (DrawingContext dc = _fans.RenderOpen())
        {
            if (!layout.IsEmpty && state.DispersionAlpha > 0.003)
            {
                DrawFans(dc, state);
            }
        }

        _fans.Opacity = state.DispersionAlpha * 0.55;
    }

    private void DrawGodRays(DrawingContext dc, IntroState state, double reach)
    {
        double spin = (state.Time - 1.25) * 5 * Math.PI / 180;
        var groups = new StreamGeometry[3];
        var contexts = new StreamGeometryContext[3];
        for (int g = 0; g < 3; g++)
        {
            groups[g] = new StreamGeometry();
            contexts[g] = groups[g].Open();
        }

        foreach (Ray ray in _rays)
        {
            double length = reach * ray.Length * state.Rays;
            double a = ray.Angle + spin;
            Wedge(contexts[ray.Group], 8, length, a, ray.HalfWidth * 0.12, ray.HalfWidth);
        }

        for (int g = 0; g < 3; g++)
        {
            contexts[g].Close();
            groups[g].Freeze();
            dc.DrawGeometry(_rayBrushes[g], null, groups[g]);
        }
    }

    private void DrawFans(DrawingContext dc, IntroState state)
    {
        double center = (FanStartDegrees + (FanSweepDegrees * state.Dispersion)) * Math.PI / 180;
        double wedge = WedgeDegrees * Math.PI / 180;
        for (int w = 0; w < FanWedges; w++)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                foreach (double axis in new[] { center, center + Math.PI })
                {
                    double a = axis + ((w - ((FanWedges - 1) / 2.0)) * wedge);
                    Wedge(ctx, 60, _brushReach, a, wedge * 0.18, wedge * 0.5);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(_fanBrushes[w], null, geometry);
        }
    }

    /// <summary>Thin quad from <paramref name="inner"/> to <paramref name="outer"/> radius around <paramref name="angle"/>.</summary>
    private static void Wedge(StreamGeometryContext ctx, double inner, double outer, double angle, double innerHalf, double outerHalf)
    {
        ctx.BeginFigure(Polar(inner, angle - innerHalf), isFilled: true, isClosed: true);
        ctx.LineTo(Polar(outer, angle - outerHalf), isStroked: false, isSmoothJoin: false);
        ctx.LineTo(Polar(outer, angle + outerHalf), isStroked: false, isSmoothJoin: false);
        ctx.LineTo(Polar(inner, angle + innerHalf), isStroked: false, isSmoothJoin: false);
    }

    private void CreateBrushes(double reach)
    {
        BrandPalette p = BrandPalette.Current;
        _brushReach = reach;

        // God rays: white-hot root → ice → iris → nothing; three intensity groups for a hand-made feel.
        double[] intensity = [1, 0.62, 0.38];
        _rayBrushes = intensity.Select(k => Radial(
            reach,
            (0, BrandPalette.WithAlpha(p.Diamond50, 0.95 * k)),
            (0.05, BrandPalette.WithAlpha(p.Ice, 0.8 * k)),
            (0.22, BrandPalette.WithAlpha(p.Aqua, 0.36 * k)),
            (0.55, BrandPalette.WithAlpha(p.Iris, 0.1 * k)),
            (1, p.Transparent))).ToArray();

        // Dispersion: one spectral color per wedge, hidden inside the diamond, brightest just outside it.
        _fanBrushes = p.Spectrum.Select(color => Radial(
            reach,
            (0, p.Transparent),
            (0.11, BrandPalette.WithAlpha(color, 0)),
            (0.17, BrandPalette.WithAlpha(color, 0.85)),
            (0.45, BrandPalette.WithAlpha(color, 0.3)),
            (1, p.Transparent))).ToArray();
    }

    private static Brush Radial(double radius, params (double Offset, Color Color)[] stops)
    {
        var brush = new RadialGradientBrush([.. stops.Select(s => new GradientStop(s.Color, s.Offset))])
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = default,
            GradientOrigin = default,
            RadiusX = radius,
            RadiusY = radius,
        };
        brush.Freeze();
        return brush;
    }

    private readonly record struct Ray(double Angle, double HalfWidth, double Length, int Group);
}
