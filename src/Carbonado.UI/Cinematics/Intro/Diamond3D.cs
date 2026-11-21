using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Carbonado.UI.Brand;
using Carbonado.UI.Effects;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The black diamond: the brilliant-cut mesh in a <see cref="Viewport3D"/> (flat-shaded obsidian body, white
/// specular, emissive heat) under a rotating rig of colored lights that strike facet glints, with a 2D bloom behind
/// it, a projected facet-edge overlay and star glints in front, spectral fringing (chromatic aberration) and the
/// shared-element flight to the logo slot (<see cref="Target"/>) at the hand-off.
/// </summary>
internal sealed class Diamond3D : IntroLayer
{
    private const double FieldOfView = 30;
    private const double CameraDistance = 5;

    private static readonly double s_focal = 1 / Math.Tan(FieldOfView * Math.PI / 360);

    private readonly BrilliantCut _cut = IntroAssets.Cut;
    private readonly double _shift;
    private readonly int[] _glintVertices;
    private readonly int[][] _vertexFacets;
    private readonly Point3D[] _world;
    private readonly Point[] _screen;
    private readonly bool[] _front;
    private readonly Viewport3D _viewport;
    private readonly DrawingVisual _glow;
    private readonly DrawingVisual _edges;
    private readonly DrawingVisual _sparkles;
    private readonly AxisAngleRotation3D _spin = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _tilt = new(new Vector3D(1, 0, 0), 18);
    private readonly ScaleTransform3D _scale = new(1, 1, 1);
    private readonly DirectionalLight _key;
    private readonly DirectionalLight _fill;
    private readonly DirectionalLight _rim;
    private readonly DirectionalLight _spark;
    private readonly SolidColorBrush _emissive;
    private ChromaticAberrationEffect? _dispersion;

    public Diamond3D()
    {
        BrandPalette p = BrandPalette.Current;
        _shift = -(_cut.Top + _cut.Bottom) / 2;
        _world = new Point3D[_cut.Vertices.Count];
        _screen = new Point[_cut.Vertices.Count];
        _front = new bool[_cut.Facets.Count];
        _vertexFacets = IntroAssets.VertexFacets;

        // Glints sit on table corners and star tips (vertices 0..7 and 8..15 of the cut), spread around the crown.
        _glintVertices = [1, 8 + 5, 6, 8 + 2, 3, 8 + 7];

        var body = new SolidColorBrush(p.Obsidian850);
        var specular = new SolidColorBrush(p.Diamond50);
        body.Freeze();
        specular.Freeze();
        _emissive = new SolidColorBrush(BrandPalette.Intensity(p.Iris, 0));
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(body));
        material.Children.Add(new SpecularMaterial(specular, 30));
        material.Children.Add(new EmissiveMaterial(_emissive));

        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(0, _shift, 0));
        transform.Children.Add(new RotateTransform3D(_spin));
        transform.Children.Add(new RotateTransform3D(_tilt));
        transform.Children.Add(_scale);

        _key = new DirectionalLight(p.Diamond50, new Vector3D(0, -1, -1));
        _fill = new DirectionalLight(p.Aqua, new Vector3D(0, -1, -1));
        _rim = new DirectionalLight(p.Rose, new Vector3D(0, -1, 1));
        _spark = new DirectionalLight(BrandPalette.Intensity(p.Ember, 0.8), new Vector3D(0, -1, -1));
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(p.Obsidian700));
        scene.Children.Add(_key);
        scene.Children.Add(_fill);
        scene.Children.Add(_rim);
        scene.Children.Add(_spark);
        scene.Children.Add(new DirectionalLight(BrandPalette.Intensity(p.Iris, 0.45), new Vector3D(-0.5, -0.8, -0.4)));
        scene.Children.Add(new GeometryModel3D(IntroAssets.Mesh, material) { Transform = transform });

        _viewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 0, CameraDistance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), FieldOfView),
            ClipToBounds = false,
            IsHitTestVisible = false,
        };
        _viewport.Children.Add(new ModelVisual3D { Content = scene });

        _glow = Add(new DrawingVisual());
        Add(_viewport);
        _edges = Add(new DrawingVisual());
        _sparkles = Add(new DrawingVisual());
        ClipToBounds = true;
        Visibility = Visibility.Hidden;
    }

    /// <summary>Logo slot to land on (overlay coordinates); null makes the diamond recede in place.</summary>
    public Rect? Target { get; set; }

    /// <summary>
    /// Uniform 2D scale that makes the landed diamond's girdle match a <see cref="DiamondMark"/> filling
    /// <paramref name="target"/>.
    /// </summary>
    public double LandingScale(in IntroLayout layout, Rect target)
    {
        // Top view (tilt 90°): the girdle plane faces the camera at depth CameraDistance − shift.
        double girdle = s_focal / (CameraDistance - _shift) * layout.DiamondRect.Width / 2;
        return girdle <= 0 ? 1 : DiamondMark.GirdleRatio * Math.Min(target.Width, target.Height) / girdle;
    }

    public override void Render(IntroState state, in IntroLayout layout)
    {
        bool visible = !layout.IsEmpty && state.DiamondOpacity > 0.002 && state.DiamondScale > 0.001;
        Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (!visible)
        {
            Effect = null;
            return;
        }

        BrandPalette p = BrandPalette.Current;
        Rect box = layout.DiamondRect;
        Opacity = state.DiamondOpacity;

        _spin.Angle = state.DiamondSpin;
        _tilt.Angle = state.DiamondTilt;
        _scale.ScaleX = _scale.ScaleY = _scale.ScaleZ = state.DiamondScale;
        AimLights(state.LightSweep * Math.PI / 180);
        double heat = state.DiamondHeat;
        _emissive.Color = BrandPalette.Intensity(BrandPalette.Mix(p.Iris, p.Diamond50, heat), Math.Max(heat, state.Emissive * 0.32));

        Project(state, box);
        DrawGlow(p, state, box);
        DrawEdges(p, state, layout.Scale);
        DrawSparkles(p, state, box);
        RenderTransform = Flight(state, layout);

        if (UseEffects && state.Fringe > 0.01)
        {
            _dispersion ??= new ChromaticAberrationEffect();
            _dispersion.Amount = state.Fringe * 1.6;
            _dispersion.Center = layout.RelativeCore;
            Effect = _dispersion;
        }
        else
        {
            Effect = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport.Measure(availableSize);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport.Arrange(new IntroLayout(finalSize.Width, finalSize.Height).DiamondRect);
        return finalSize;
    }

    private void AimLights(double sweep)
    {
        // Directions are the light's travel; the rig orbits the vertical axis so facets catch it in turn.
        _key.Direction = new Vector3D(-0.8 * Math.Cos(sweep), -0.65, -0.7 - (0.3 * Math.Sin(sweep)));
        _fill.Direction = new Vector3D(-0.9 * Math.Cos(sweep + 2.3), 0.25, -0.6);
        _rim.Direction = new Vector3D(-0.7 * Math.Cos(sweep + 4.2), -0.4, 0.65);
        _spark.Direction = new Vector3D(0.6 * Math.Sin(sweep * 1.7), -0.9 * Math.Cos(sweep * 0.6), -0.5);
    }

    /// <summary>Applies the model transform on the CPU (same order as the 3D group) and projects like the camera.</summary>
    private void Project(IntroState state, Rect box)
    {
        double phi = state.DiamondSpin * Math.PI / 180, alpha = state.DiamondTilt * Math.PI / 180, s = state.DiamondScale;
        double cp = Math.Cos(phi), sp = Math.Sin(phi), ca = Math.Cos(alpha), sa = Math.Sin(alpha);
        for (int v = 0; v < _world.Length; v++)
        {
            Point3D m = _cut.Vertices[v];
            double y0 = m.Y + _shift;
            double x1 = (m.X * cp) + (m.Z * sp), z1 = (-m.X * sp) + (m.Z * cp);
            double y2 = (y0 * ca) - (z1 * sa), z2 = (y0 * sa) + (z1 * ca);
            var w = new Point3D(x1 * s, y2 * s, z2 * s);
            _world[v] = w;
            double depth = CameraDistance - w.Z;
            _screen[v] = new Point(
                box.X + (((w.X * s_focal / depth) + 1) * box.Width / 2),
                box.Y + ((1 - (w.Y * s_focal / depth)) * box.Height / 2));
        }

        for (int f = 0; f < _front.Length; f++)
        {
            Vector3D n = _cut.Normals[f];
            double nx = (n.X * cp) + (n.Z * sp), nz1 = (-n.X * sp) + (n.Z * cp);
            double ny = (n.Y * ca) - (nz1 * sa), nz = (n.Y * sa) + (nz1 * ca);
            Point3D a = _world[_cut.Facets[f][0]];
            _front[f] = (nx * a.X) + (ny * a.Y) + (nz * (a.Z - CameraDistance)) < 0;
        }
    }

    private void DrawGlow(BrandPalette p, IntroState state, Rect box)
    {
        using DrawingContext dc = _glow.RenderOpen();
        var center = new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        double grown = 0.55 + (0.45 * Math.Min(1, state.DiamondScale));
        if (state.DiamondGlow > 0.002)
        {
            double g = state.DiamondGlow;
            var bloom = new RadialGradientBrush(
                [
                    new GradientStop(BrandPalette.WithAlpha(p.Iris, 0.5 * g), 0),
                    new GradientStop(BrandPalette.WithAlpha(p.Aqua, 0.16 * g), 0.45),
                    new GradientStop(p.Transparent, 1),
                ]);
            bloom.Freeze();
            double r = box.Width * 0.8 * grown;
            dc.DrawEllipse(bloom, null, center, r, r);
        }

        if (state.DiamondHeat > 0.002)
        {
            double h = state.DiamondHeat;
            var hot = new RadialGradientBrush(
                [
                    new GradientStop(BrandPalette.WithAlpha(p.Diamond50, 0.9 * h), 0),
                    new GradientStop(BrandPalette.WithAlpha(p.Ice, 0.45 * h), 0.35),
                    new GradientStop(p.Transparent, 1),
                ]);
            hot.Freeze();
            double r = box.Width * 0.46 * grown;
            dc.DrawEllipse(hot, null, center, r, r);
        }
    }

    private void DrawEdges(BrandPalette p, IntroState state, double scale)
    {
        using DrawingContext dc = _edges.RenderOpen();
        double alpha = Clamp01(state.DiamondScale * 1.3) * (1 - (0.85 * state.DiamondHeat));
        if (alpha <= 0.01)
        {
            return;
        }

        var inner = new StreamGeometry();
        var rim = new StreamGeometry();
        using (StreamGeometryContext innerCtx = inner.Open(), rimCtx = rim.Open())
        {
            foreach (var (a, b, left, right) in _cut.Edges)
            {
                bool l = _front[left], r = _front[right];
                if (!l && !r)
                {
                    continue;
                }

                StreamGeometryContext ctx = l && r ? innerCtx : rimCtx;
                ctx.BeginFigure(_screen[a], isFilled: false, isClosed: false);
                ctx.LineTo(_screen[b], isStroked: true, isSmoothJoin: false);
            }
        }

        inner.Freeze();
        rim.Freeze();
        var innerPen = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(p.Diamond50, 0.3 * alpha)), Math.Max(0.5, 0.8 * scale));
        var rimPen = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(BrandPalette.Mix(p.Ice, p.Diamond50, 0.3), 0.75 * alpha)), Math.Max(0.7, 1.4 * scale))
        {
            LineJoin = PenLineJoin.Round,
        };
        innerPen.Freeze();
        rimPen.Freeze();
        dc.DrawGeometry(null, innerPen, inner);
        dc.DrawGeometry(null, rimPen, rim);
    }

    private void DrawSparkles(BrandPalette p, IntroState state, Rect box)
    {
        using DrawingContext dc = _sparkles.RenderOpen();
        for (int g = 0; g < IntroState.GlintCount; g++)
        {
            double q = state.Glints[g];
            if (q <= 0 || q >= 1)
            {
                continue;
            }

            int vertex = _glintVertices[g];
            if (!Array.Exists(_vertexFacets[vertex], f => _front[f]))
            {
                continue;
            }

            double pulse = Math.Sin(Math.PI * q);
            DiamondMark.DrawFlare(dc, p, _screen[vertex], box.Width * 0.2 * (0.6 + (0.4 * pulse)), Math.Pow(pulse, 1.3));
        }
    }

    /// <summary>Hand-off: rise-then-glide arc to the logo slot, shrinking in log space to the mark's size.</summary>
    private Transform Flight(IntroState state, in IntroLayout layout)
    {
        double f = state.Flight;
        if (f <= 0)
        {
            return Transform.Identity;
        }

        Point from = layout.Core;
        Point to = from;
        double k = 1 - (0.25 * f);
        if (Target is { IsEmpty: false } target && target.Width > 0 && target.Height > 0)
        {
            to = new Point(target.X + (target.Width / 2), target.Y + (target.Height / 2));
            k = Math.Exp(Math.Log(LandingScale(layout, target)) * f);
        }

        // Quadratic Bézier with the control point above the start: the diamond lifts off, then glides over.
        var control = new Point(from.X, to.Y);
        double u = 1 - f;
        var at = new Point(
            (u * u * from.X) + (2 * u * f * control.X) + (f * f * to.X),
            (u * u * from.Y) + (2 * u * f * control.Y) + (f * f * to.Y));
        var matrix = new Matrix(k, 0, 0, k, at.X - (k * from.X), at.Y - (k * from.Y));
        var transform = new MatrixTransform(matrix);
        transform.Freeze();
        return transform;
    }
}
