using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Carbonado.UI.Cinematics.Intro;

namespace Carbonado.UI.Brand;

/// <summary>
/// The Carbonado mark: a brilliant-cut black diamond seen from above (octagonal table, star, bezel and upper-girdle
/// facets) with a prismatic girdle highlight. Vector, transparent background, square-fitted and centered; detail
/// adapts to the rendered pixel size so it stays crisp from 16 to 256 px (title bar, app icon). <see cref="Glint"/>
/// adds a star flare and brightens the crown (hover, intro landing).
/// </summary>
public sealed class DiamondMark : FrameworkElement
{
    /// <summary>Identifies the <see cref="Glint"/> dependency property.</summary>
    public static readonly DependencyProperty GlintProperty = DependencyProperty.Register(
        nameof(Glint), typeof(double), typeof(DiamondMark),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceUnit));

    /// <summary>Girdle radius as a fraction of the mark's side (room for the rim and the flare).</summary>
    internal const double GirdleRatio = 0.45;

    private const double DefaultSize = 32;

    // Light from the upper left and above (top view: screen x = model X, screen y = model Z, up = −Z).
    private static readonly Vector3D s_light = Normalized(new Vector3D(-0.5, 0.75, -0.6));
    private static readonly Vector3D s_half = Normalized(s_light + new Vector3D(0, 1, 0));
    private static readonly Lazy<CrownFacet[]> s_crown = new(BuildCrown, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Builds the crown facets on the thread pool (pure geometry math) so the first title-bar render skips it.</summary>
    public static void PrepareInBackground() => ThreadPool.QueueUserWorkItem(static _ =>
    {
        try
        {
            _ = s_crown.Value;
        }
        catch (Exception)
        {
            // Never take the process down from a warm-up: the Lazy keeps the failure and the first render reports it.
        }
    });

    /// <summary>Sparkle intensity 0..1 (0 = at rest).</summary>
    public double Glint
    {
        get => (double)GetValue(GlintProperty);
        set => SetValue(GlintProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultSize : Math.Min(DefaultSize, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? DefaultSize : Math.Min(DefaultSize, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        double devicePixels = side * VisualTreeHelper.GetDpi(this).DpiScaleX;
        Draw(drawingContext, new Point(ActualWidth / 2, ActualHeight / 2), side * GirdleRatio, devicePixels, Glint);
    }

    /// <summary>
    /// Draws the mark centered on <paramref name="center"/> with girdle radius <paramref name="radius"/> (DIPs);
    /// <paramref name="devicePixels"/> is the rendered side in device pixels and selects the level of detail.
    /// </summary>
    private static void Draw(DrawingContext dc, Point center, double radius, double devicePixels, double glint)
    {
        BrandPalette p = BrandPalette.Current;
        double px = radius / (devicePixels * GirdleRatio);   // one device pixel in DIPs
        bool detailed = devicePixels >= 40;
        bool tiny = devicePixels < 22;

        Point At(Point unit) => new(center.X + (unit.X * radius), center.Y + (unit.Y * radius));

        Geometry girdle = Polygon(Girdle, At);
        var body = new RadialGradientBrush(BrandPalette.Mix(p.Obsidian800, p.Iris, 0.1), p.Obsidian950)
        {
            GradientOrigin = new Point(0.3, 0.25),
            Center = new Point(0.4, 0.35),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        body.Freeze();
        dc.DrawGeometry(body, null, girdle);

        // Tiny marks boost the facet contrast: the lit crown against the dark table is what reads at 16 px.
        foreach (CrownFacet facet in s_crown.Value)
        {
            if (facet.Kind == FacetKind.Table)
            {
                continue;
            }

            var brush = new SolidColorBrush(Shade(p, facet, tiny ? Math.Max(glint, 0.35) : glint));
            brush.Freeze();
            dc.DrawGeometry(brush, null, Polygon(facet.Outline, At));
        }

        // Black glass table with a cool reflection from the light corner.
        Geometry table = Polygon(Table, At);
        var glass = new LinearGradientBrush(
            [
                new GradientStop(BrandPalette.Mix(p.Obsidian700, p.Ice, 0.18 + (0.35 * glint)), 0),
                new GradientStop(BrandPalette.Mix(p.Obsidian850, p.Iris, 0.12 + (0.2 * glint)), 0.45),
                new GradientStop(p.Obsidian950, 1),
            ],
            new Point(0.15, 0.1),
            new Point(0.85, 0.9));
        glass.Freeze();
        Pen? tableEdge = null;
        if (!tiny)
        {
            tableEdge = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(BrandPalette.Mix(p.Diamond50, p.Ice, 0.3), 0.42)), Math.Max(px, radius * 0.018));
            tableEdge.Freeze();
        }

        dc.DrawGeometry(glass, tableEdge, table);

        if (detailed)
        {
            var edge = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(p.Diamond50, 0.22)), Math.Max(px * 0.8, radius * 0.01));
            edge.Freeze();
            foreach (CrownFacet facet in s_crown.Value)
            {
                if (facet.Kind != FacetKind.Table)
                {
                    dc.DrawGeometry(null, edge, Polygon(facet.Outline, At));
                }
            }
        }

        // Prismatic girdle highlight, lit from the upper left: Ice → Aqua → Iris → Rose.
        var prisma = new LinearGradientBrush(
            [
                new GradientStop(p.Ice, 0),
                new GradientStop(p.Aqua, 0.3),
                new GradientStop(p.Iris, 0.62),
                new GradientStop(p.Rose, 1),
            ],
            new Point(0.15, 0.05),
            new Point(0.85, 0.95));
        prisma.Freeze();
        var rim = new Pen(prisma, Math.Max(px * 1.1, radius * 0.045)) { LineJoin = PenLineJoin.Round };
        rim.Freeze();
        dc.DrawGeometry(null, rim, girdle);

        if (glint > 0.001)
        {
            // Upper-left table corner (225°), the corner nearest to the light.
            double corner = 1.25 * Math.PI;
            Point tip = At(new Point(BrilliantCut.TableRadius * Math.Cos(corner), BrilliantCut.TableRadius * Math.Sin(corner)));
            DrawFlare(dc, p, tip, radius * 1.1 * glint, glint);
        }
    }

    /// <summary>Four-point star flare with a soft core (also used for the intro's facet glints).</summary>
    internal static void DrawFlare(DrawingContext dc, BrandPalette p, Point at, double size, double intensity)
    {
        if (size <= 0 || intensity <= 0)
        {
            return;
        }

        var glow = new RadialGradientBrush(
            [
                new GradientStop(BrandPalette.WithAlpha(p.Diamond50, intensity), 0),
                new GradientStop(BrandPalette.WithAlpha(p.Ice, 0.6 * intensity), 0.2),
                new GradientStop(BrandPalette.WithAlpha(p.Aqua, 0.2 * intensity), 0.5),
                new GradientStop(p.Transparent, 1),
            ]);
        glow.Freeze();
        double thin = size * 0.06;
        var rays = new StreamGeometry();
        using (StreamGeometryContext ctx = rays.Open())
        {
            void Spike(double dx, double dy, double nx, double ny)
            {
                ctx.BeginFigure(new Point(at.X - dx, at.Y - dy), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(at.X + nx, at.Y + ny), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(at.X + dx, at.Y + dy), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(at.X - nx, at.Y - ny), isStroked: false, isSmoothJoin: false);
            }

            Spike(size, 0, 0, thin);
            Spike(0, size, thin, 0);
            double d = size * 0.4 * Math.Sqrt(0.5), dt = thin * 0.6 * Math.Sqrt(0.5);
            Spike(d, d, dt, -dt);
            Spike(d, -d, dt, dt);
        }

        rays.Freeze();
        dc.DrawGeometry(glow, null, rays);
        dc.DrawEllipse(glow, null, at, size * 0.3, size * 0.3);
    }

    /// <summary>Girdle outline in unit coordinates (16-gon of the brilliant cut).</summary>
    private static Point[] Girdle { get; } = Enumerable.Range(0, 16).Select(j => new Point(Math.Cos(j * Math.PI / 8), Math.Sin(j * Math.PI / 8))).ToArray();

    /// <summary>Table outline in unit coordinates (octagon).</summary>
    private static Point[] Table { get; } = Enumerable.Range(0, 8)
        .Select(k => new Point(BrilliantCut.TableRadius * Math.Cos(k * Math.PI / 4), BrilliantCut.TableRadius * Math.Sin(k * Math.PI / 4)))
        .ToArray();

    /// <summary>
    /// Facet tone: a lambert term for depth, a tight specular lobe for fire and a light/dark pinwheel on the upper
    /// girdle (the classic brilliant pattern), tinted along the cool spectrum by azimuth.
    /// </summary>
    private static Color Shade(BrandPalette p, CrownFacet facet, double glint)
    {
        double lambert = Math.Max(0, Vector3D.DotProduct(facet.Normal, s_light));
        double spec = Math.Pow(Math.Max(0, Vector3D.DotProduct(facet.Normal, s_half)), 40);
        double tone = facet.Kind switch
        {
            FacetKind.Star => 0.28 + (0.55 * lambert * lambert),
            FacetKind.Bezel => 0.1 + (0.45 * lambert * lambert * lambert),
            _ => (facet.Leading ? 0.42 : 0.08) + (0.4 * lambert * lambert),
        };
        Color tint = CoolSpectrum(p, facet.Azimuth);
        Color color = BrandPalette.Mix(p.Obsidian950, tint, Math.Clamp(tone + (0.25 * glint), 0, 1));
        return BrandPalette.Mix(color, p.Diamond50, Math.Min(0.8, (0.7 * spec) + (0.15 * glint)));
    }

    /// <summary>Ice (upper left, toward the light) → Aqua → Iris → Orchid → Rose (lower right).</summary>
    private static Color CoolSpectrum(BrandPalette p, double azimuth)
    {
        Color[] stops = [p.Ice, p.Aqua, p.Iris, p.Orchid, p.Rose];
        double fromLight = Math.Abs(Math.IEEERemainder(azimuth - (1.25 * Math.PI), 2 * Math.PI)) / Math.PI;   // 0 at the light, 1 opposite
        double u = fromLight * (stops.Length - 1);
        int i = Math.Min((int)u, stops.Length - 2);
        return BrandPalette.Mix(stops[i], stops[i + 1], u - i);
    }

    private static Geometry Polygon(Point[] unit, Func<Point, Point> map)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(map(unit[0]), isFilled: true, isClosed: true);
            for (int i = 1; i < unit.Length; i++)
            {
                ctx.LineTo(map(unit[i]), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static CrownFacet[] BuildCrown()
    {
        BrilliantCut cut = BrilliantCut.Build(16);
        var crown = new List<CrownFacet>();
        for (int f = 0; f < cut.Facets.Count; f++)
        {
            FacetKind kind = cut.Kinds[f];
            if (kind is not (FacetKind.Table or FacetKind.Star or FacetKind.Bezel or FacetKind.UpperGirdle))
            {
                continue;
            }

            Point[] outline = cut.Facets[f].Select(v => new Point(cut.Vertices[v].X, cut.Vertices[v].Z)).ToArray();
            double azimuth = Math.Atan2(outline.Average(q => q.Y), outline.Average(q => q.X));

            // Upper-girdle pairs flank a star tip at (2k+1)·22.5°: the facet after the tip (clockwise) leads.
            double sector = (azimuth / (Math.PI / 4)) - 0.5;
            bool leading = sector - Math.Floor(sector) >= 0.5;
            crown.Add(new CrownFacet(kind, outline, cut.Normals[f], azimuth, leading));
        }

        return [.. crown];
    }

    private static Vector3D Normalized(Vector3D v)
    {
        v.Normalize();
        return v;
    }

    private static object CoerceUnit(DependencyObject d, object value) => Math.Clamp((double)value, 0, 1);

    private readonly record struct CrownFacet(FacetKind Kind, Point[] Outline, Vector3D Normal, double Azimuth, bool Leading);
}
