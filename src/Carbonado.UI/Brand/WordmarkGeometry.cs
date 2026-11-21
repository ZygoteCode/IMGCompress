using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Brand;

/// <summary>
/// The CARBONADO wordmark as custom geometric letterforms: monoline strokes whose corners are cut at 45° like gem
/// facets (chamfered bowls, faceted terminals, a flat-cut apex). Each letter is a frozen <see cref="Geometry"/> in
/// its own em box: x from 0 to its advance, y from 0 (cap line) to <see cref="CapHeight"/> (baseline).
/// </summary>
public static class WordmarkGeometry
{
    /// <summary>The spelled word.</summary>
    public const string Text = "CARBONADO";

    /// <summary>Cap height of every letter in geometry units (the em of the tracking values).</summary>
    public const double CapHeight = 100;

    /// <summary>Resting letter spacing in em (fraction of <see cref="CapHeight"/>).</summary>
    public const double RestTracking = 0.18;

    private const double Stroke = 12;

    private static readonly Lazy<(Geometry[] Letters, double[] Advances)> s_glyphs = new(BuildGlyphs, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The nine letters C, A, R, B, O, N, A, D, O (repeated letters share one frozen geometry).</summary>
    public static IReadOnlyList<Geometry> Letters => s_glyphs.Value.Letters;

    /// <summary>Advance width of each letter in geometry units (letter box width, spacing excluded).</summary>
    public static IReadOnlyList<double> Advances => s_glyphs.Value.Advances;

    /// <summary>Total width of the word set with <paramref name="trackingEm"/> between letters.</summary>
    public static double Width(double trackingEm = RestTracking) =>
        s_glyphs.Value.Advances.Sum() + ((Text.Length - 1) * trackingEm * CapHeight);

    /// <summary>X offset of letter <paramref name="index"/> when set with <paramref name="trackingEm"/>.</summary>
    public static double Offset(int index, double trackingEm = RestTracking)
    {
        double[] advances = s_glyphs.Value.Advances;
        double x = 0;
        for (int i = 0; i < index; i++)
        {
            x += advances[i] + (trackingEm * CapHeight);
        }

        return x;
    }

    /// <summary>The whole word as one frozen geometry (origin at the left cap line).</summary>
    public static Geometry Compose(double trackingEm = RestTracking)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int i = 0; i < Text.Length; i++)
        {
            var placed = Letters[i].Clone();
            placed.Transform = new TranslateTransform(Offset(i, trackingEm), 0);
            group.Children.Add(placed);
        }

        group.Freeze();
        return group;
    }

    private static (Geometry[], double[]) BuildGlyphs()
    {
        var glyphs = new Dictionary<char, (Geometry Shape, double Advance)>
        {
            ['C'] = C(78),
            ['A'] = A(84),
            ['R'] = R(78),
            ['B'] = B(74),
            ['O'] = O(86),
            ['N'] = N(78),
            ['D'] = D(80),
        };

        // Outlined geometry has normalized windings: it fills identically under either fill rule (GeometryGroup,
        // Path, DrawGeometry) and in any composition.
        var outlined = glyphs.ToDictionary(g => g.Key, g =>
        {
            PathGeometry outline = g.Value.Shape.GetOutlinedPathGeometry();
            outline.Freeze();
            return outline;
        });
        var letters = new Geometry[Text.Length];
        var advances = new double[Text.Length];
        for (int i = 0; i < Text.Length; i++)
        {
            letters[i] = outlined[Text[i]];
            advances[i] = glyphs[Text[i]].Advance;
        }

        return (letters, advances);
    }

    /// <summary>Inner chamfer that keeps the stroke weight constant across a 45° cut.</summary>
    private static double Inner(double chamfer) => Math.Max(0, chamfer - (Stroke * (2 - Math.Sqrt(2))));

    private static (Geometry, double) O(double w)
    {
        const double c = 26;
        return (Polygons(FillRule.EvenOdd, Box(0, 0, w, CapHeight, c, c, c, c), Box(Stroke, Stroke, w - Stroke, CapHeight - Stroke, Inner(c), Inner(c), Inner(c), Inner(c))), w);
    }

    private static (Geometry, double) C(double w)
    {
        const double c = 26, s = Stroke, h = CapHeight;
        double ci = Inner(c);

        // Open bowl with terminals cut parallel to the chamfers.
        Point[] outline =
        [
            new(w, 0), new(w - s, s), new(s + ci, s), new(s, s + ci), new(s, h - s - ci), new(s + ci, h - s), new(w - s, h - s),
            new(w, h), new(c, h), new(0, h - c), new(0, c), new(c, 0),
        ];
        return (Polygons(FillRule.Nonzero, outline), w);
    }

    private static (Geometry, double) A(double w)
    {
        const double h = CapHeight, apex = 9, barTop = 60, barBottom = barTop + Stroke - 1;
        double mid = w / 2;
        double run = mid - apex;                                       // horizontal run of each leg over the cap height
        double leg = Stroke * Math.Sqrt(1 + ((run / h) * (run / h)));   // horizontal thickness of a diagonal stroke
        double InnerLeft(double y) => leg + (run * (h - y) / h);
        double InnerRight(double y) => w - InnerLeft(y);
        double innerApex = h - ((w - (2 * leg)) * h / (2 * run));

        Point[] outer =
        [
            new(0, h), new(mid - apex, 0), new(mid + apex, 0), new(w, h), new(w - leg, h), new(InnerRight(barBottom), barBottom),
            new(InnerLeft(barBottom), barBottom), new(leg, h),
        ];
        Point[] counter = [new(mid, innerApex), new(InnerRight(barTop), barTop), new(InnerLeft(barTop), barTop)];
        return (Polygons(FillRule.EvenOdd, outer, counter), w);
    }

    private static (Geometry, double) R(double w)
    {
        const double h = CapHeight, s = Stroke, bowl = 58, ct = 22, cb = 18;
        double bowlRight = w - 2;
        Geometry ring = Polygons(
            FillRule.EvenOdd,
            Box(0, 0, bowlRight, bowl, 0, ct, cb, 0),
            Box(s, s, bowlRight - s, bowl - s, 0, Inner(ct), Inner(cb), 0));
        Geometry stem = Polygons(FillRule.Nonzero, Box(0, 0, s, h, 0, 0, 0, 0));

        // Diagonal leg from the bowl to the baseline, rooted inside the bowl's lower bar.
        double rootRight = bowlRight - cb - 2, top = bowl - s;
        double slope = (w - rootRight) / (h - bowl);
        double thick = s * Math.Sqrt(1 + (slope * slope));
        Point[] legShape =
        [
            new(rootRight - (slope * s) - thick, top), new(rootRight - (slope * s), top), new(w, h), new(w - thick, h),
        ];
        return (Union(stem, ring, Polygons(FillRule.Nonzero, legShape)), w);
    }

    private static (Geometry, double) B(double w)
    {
        const double h = CapHeight, s = Stroke, waist = 46, c = 18;
        double upperRight = w - 6;
        Geometry upper = Polygons(
            FillRule.EvenOdd,
            Box(0, 0, upperRight, waist + s, 0, c, c - 4, 0),
            Box(s, s, upperRight - s, waist, 0, Inner(c), Inner(c - 4), 0));
        Geometry lower = Polygons(
            FillRule.EvenOdd,
            Box(0, waist, w, h, 0, c, c + 4, 0),
            Box(s, waist + s, w - s, h - s, 0, Inner(c), Inner(c + 4), 0));
        return (Union(upper, lower), w);
    }

    private static (Geometry, double) N(double w)
    {
        const double h = CapHeight, s = Stroke, cut = 7;

        // Horizontal thickness of a diagonal of weight s spanning the full letter box (fixed point, converges fast).
        double thick = s;
        for (int i = 0; i < 6; i++)
        {
            double slope = (w - thick) / h;
            thick = s * Math.Sqrt(1 + (slope * slope));
        }

        Geometry stems = Polygons(FillRule.Nonzero, Box(0, 0, s, h, cut, 0, 0, 0), Box(w - s, 0, w, h, 0, 0, cut, 0));
        Point[] diagonal = [new(0, 0), new(thick, 0), new(w, h), new(w - thick, h)];
        Geometry clipped = Geometry.Combine(Polygons(FillRule.Nonzero, diagonal), Polygons(FillRule.Nonzero, Box(0, 0, w, h, cut, 0, cut, 0)), GeometryCombineMode.Intersect, null);
        return (Union(stems, clipped), w);
    }

    private static (Geometry, double) D(double w)
    {
        const double c = 32;
        return (Polygons(FillRule.EvenOdd, Box(0, 0, w, CapHeight, 0, c, c, 0), Box(Stroke, Stroke, w - Stroke, CapHeight - Stroke, 0, Inner(c), Inner(c), 0)), w);
    }

    /// <summary>Rectangle with independent 45° corner cuts (top-left, top-right, bottom-right, bottom-left), clockwise.</summary>
    private static Point[] Box(double x0, double y0, double x1, double y1, double tl, double tr, double br, double bl)
    {
        var points = new List<Point>(8);
        void Corner(double cut, Point before, Point corner, Point after)
        {
            if (cut <= 0)
            {
                points.Add(corner);
                return;
            }

            points.Add(before);
            points.Add(after);
        }

        Corner(tl, new Point(x0, y0 + tl), new Point(x0, y0), new Point(x0 + tl, y0));
        Corner(tr, new Point(x1 - tr, y0), new Point(x1, y0), new Point(x1, y0 + tr));
        Corner(br, new Point(x1, y1 - br), new Point(x1, y1), new Point(x1 - br, y1));
        Corner(bl, new Point(x0 + bl, y1), new Point(x0, y1), new Point(x0, y1 - bl));
        return [.. points];
    }

    private static Geometry Polygons(FillRule rule, params Point[][] figures)
    {
        var geometry = new StreamGeometry { FillRule = rule };
        using (StreamGeometryContext ctx = geometry.Open())
        {
            foreach (Point[] figure in figures)
            {
                ctx.BeginFigure(figure[0], isFilled: true, isClosed: true);
                ctx.PolyLineTo(figure[1..], isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry Union(params Geometry[] parts)
    {
        Geometry result = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            result = Geometry.Combine(result, parts[i], GeometryCombineMode.Union, null);
        }

        result.Freeze();
        return result;
    }
}
