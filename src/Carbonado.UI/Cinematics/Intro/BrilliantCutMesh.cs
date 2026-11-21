using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Round brilliant cut as a flat-shaded <see cref="MeshGeometry3D"/>: octagonal table, star, bezel and upper-girdle
/// facets on the crown, a faceted girdle, lower-girdle and main facets on the pavilion and a small culet. Girdle
/// radius 1, girdle plane at y = 0, table up (+Y); Tolkowsky-like proportions (table 55 %, crown angle 34.5°,
/// pavilion angle 40.75°). Every facet owns its vertices so normals are per facet (crisp glints).
/// </summary>
public static class BrilliantCutMesh
{
    /// <summary>Builds the frozen mesh.</summary>
    /// <param name="segments">Girdle vertices: even and ≥ 6 (16 gives the classic 57-facet brilliant + girdle facets).</param>
    public static MeshGeometry3D Create(int segments = 16)
    {
        BrilliantCut cut = BrilliantCut.Build(segments);
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();
        for (int f = 0; f < cut.Facets.Count; f++)
        {
            int[] loop = cut.Facets[f];
            Vector3D normal = cut.Normals[f];
            int first = positions.Count;
            foreach (int v in loop)
            {
                positions.Add(cut.Vertices[v]);
                normals.Add(normal);
            }

            for (int i = 1; i < loop.Length - 1; i++)
            {
                indices.Add(first);
                indices.Add(first + i);
                indices.Add(first + i + 1);
            }
        }

        var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };
        mesh.Freeze();
        return mesh;
    }
}

/// <summary>Facet families of the brilliant cut (crown first, top to bottom).</summary>
internal enum FacetKind
{
    Table,
    Star,
    Bezel,
    UpperGirdle,
    Girdle,
    LowerGirdle,
    PavilionMain,
    Culet,
}

/// <summary>
/// Welded brilliant-cut polyhedron: shared vertices, facets as counter-clockwise (seen from outside) vertex loops,
/// unit outward normals and the edge list with both adjacent facets. Convex by construction: every kite/pentagon
/// is planar because its symmetric vertex pairs are placed on the plane through its axis line.
/// </summary>
internal sealed class BrilliantCut
{
    /// <summary>Table vertex radius (girdle radius = 1) for girdles of 16 or more segments.</summary>
    public const double TableRadius = 0.55;

    /// <summary>Radius of the star-facet tips for girdles of 16 or more segments.</summary>
    public const double StarRadius = 0.78;

    /// <summary>Half thickness of the girdle.</summary>
    public const double GirdleHalfHeight = 0.022;

    private const double CrownAngleDegrees = 34.5;
    private const double PavilionAngleDegrees = 40.75;
    private const double LowerGirdleRadius = 0.24;
    private const double CuletRadius = 0.035;

    private static readonly Dictionary<int, BrilliantCut> s_cache = [];

    private BrilliantCut(int segments)
    {
        int m = segments / 2;
        double half = Math.PI / m;
        double crownSlope = Math.Tan(CrownAngleDegrees * Math.PI / 180);
        double pavilionSlope = Math.Tan(PavilionAngleDegrees * Math.PI / 180);
        double g = GirdleHalfHeight;
        // Coarse girdles need a smaller table and longer stars: a star tip must project outside the table edge
        // (along the bezel axis) or the crown turns concave.
        double tableRadius = Math.Min(TableRadius, 0.8 * Math.Cos(half));
        double starRadius = Math.Max(StarRadius, (tableRadius / Math.Cos(half)) + 0.06);
        double tableY = g + ((1 - tableRadius) * crownSlope);

        var vertices = new List<Point3D>(2 * segments + 4 * m);
        int table = vertices.Count;
        for (int k = 0; k < m; k++)
        {
            vertices.Add(Polar(tableRadius, 2 * k * half, tableY));
        }

        // Star tips sit on the bezel planes: height of the bezel axis line at their projected radius.
        int star = vertices.Count;
        double starAxis = starRadius * Math.Cos(half);
        double starY = tableY + ((g - tableY) * (starAxis - tableRadius) / (1 - tableRadius));
        for (int k = 0; k < m; k++)
        {
            vertices.Add(Polar(starRadius, (2 * k + 1) * half, starY));
        }

        int girdleTop = vertices.Count;
        for (int j = 0; j < segments; j++)
        {
            vertices.Add(Polar(1, j * half, g));
        }

        int girdleBottom = vertices.Count;
        for (int j = 0; j < segments; j++)
        {
            vertices.Add(Polar(1, j * half, -g));
        }

        // Pavilion mains are planes through the girdle point and the axis line of slope pavilionSlope.
        double PavilionY(double axisRadius) => -g - (pavilionSlope * (1 - axisRadius));
        int lower = vertices.Count;
        for (int k = 0; k < m; k++)
        {
            vertices.Add(Polar(LowerGirdleRadius, (2 * k + 1) * half, PavilionY(LowerGirdleRadius * Math.Cos(half))));
        }

        int culet = vertices.Count;
        double culetY = PavilionY(CuletRadius * Math.Cos(half));
        for (int k = 0; k < m; k++)
        {
            vertices.Add(Polar(CuletRadius, (2 * k + 1) * half, culetY));
        }

        Vertices = vertices;
        Top = tableY;
        Bottom = culetY;

        var facets = new List<int[]>();
        var kinds = new List<FacetKind>();
        void Add(FacetKind kind, params int[] loop)
        {
            facets.Add(loop);
            kinds.Add(kind);
        }

        int T(int k) => table + Mod(k, m);
        int S(int k) => star + Mod(k, m);
        int G(int j) => girdleTop + Mod(j, segments);
        int B(int j) => girdleBottom + Mod(j, segments);
        int L(int k) => lower + Mod(k, m);
        int C(int k) => culet + Mod(k, m);

        Add(FacetKind.Table, Enumerable.Range(0, m).Select(T).ToArray());
        for (int k = 0; k < m; k++)
        {
            Add(FacetKind.Star, T(k), S(k), T(k + 1));
            Add(FacetKind.Bezel, T(k), S(k - 1), G(2 * k), S(k));
            Add(FacetKind.UpperGirdle, S(k), G(2 * k), G(2 * k + 1));
            Add(FacetKind.UpperGirdle, S(k), G(2 * k + 1), G(2 * k + 2));
        }

        for (int j = 0; j < segments; j++)
        {
            Add(FacetKind.Girdle, G(j), B(j), B(j + 1), G(j + 1));
        }

        for (int k = 0; k < m; k++)
        {
            Add(FacetKind.LowerGirdle, L(k), B(2 * k + 1), B(2 * k));
            Add(FacetKind.LowerGirdle, L(k), B(2 * k + 2), B(2 * k + 1));
            Add(FacetKind.PavilionMain, B(2 * k), L(k - 1), C(k - 1), C(k), L(k));
        }

        Add(FacetKind.Culet, Enumerable.Range(0, m).Select(k => C(m - 1 - k)).ToArray());

        var normals = new List<Vector3D>(facets.Count);
        for (int f = 0; f < facets.Count; f++)
        {
            Vector3D n = NewellNormal(vertices, facets[f]);
            // Orient outwards (the solid is convex and contains the girdle center).
            if (Vector3D.DotProduct(n, (Vector3D)Centroid(vertices, facets[f])) < 0)
            {
                Array.Reverse(facets[f]);
                n = -n;
            }

            normals.Add(n);
        }

        Facets = facets;
        Kinds = kinds;
        Normals = normals;
        Edges = BuildEdges(facets);
    }

    public IReadOnlyList<Point3D> Vertices { get; }

    public IReadOnlyList<int[]> Facets { get; }

    public IReadOnlyList<FacetKind> Kinds { get; }

    public IReadOnlyList<Vector3D> Normals { get; }

    /// <summary>Unique edges (A &lt; B) with the two facets sharing them.</summary>
    public IReadOnlyList<(int A, int B, int Left, int Right)> Edges { get; }

    /// <summary>Table height.</summary>
    public double Top { get; }

    /// <summary>Culet height.</summary>
    public double Bottom { get; }

    /// <summary>Shared (cached) model for <paramref name="segments"/> girdle vertices.</summary>
    public static BrilliantCut Build(int segments)
    {
        if (segments < 6 || segments % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segments), segments, "Segments must be even and at least 6.");
        }

        lock (s_cache)
        {
            if (!s_cache.TryGetValue(segments, out BrilliantCut? cut))
            {
                cut = new BrilliantCut(segments);
                s_cache[segments] = cut;
            }

            return cut;
        }
    }

    private static Point3D Polar(double radius, double angle, double y) =>
        new(radius * Math.Cos(angle), y, radius * Math.Sin(angle));

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private static Point3D Centroid(List<Point3D> vertices, int[] loop)
    {
        double x = 0, y = 0, z = 0;
        foreach (int v in loop)
        {
            x += vertices[v].X;
            y += vertices[v].Y;
            z += vertices[v].Z;
        }

        return new Point3D(x / loop.Length, y / loop.Length, z / loop.Length);
    }

    /// <summary>Newell's method: robust unit normal of a planar polygon, following the loop's winding.</summary>
    private static Vector3D NewellNormal(List<Point3D> vertices, int[] loop)
    {
        var n = default(Vector3D);
        for (int i = 0; i < loop.Length; i++)
        {
            Point3D a = vertices[loop[i]], b = vertices[loop[(i + 1) % loop.Length]];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }

        n.Normalize();
        return n;
    }

    private static List<(int, int, int, int)> BuildEdges(List<int[]> facets)
    {
        var map = new Dictionary<(int, int), (int Left, int Right)>();
        for (int f = 0; f < facets.Count; f++)
        {
            int[] loop = facets[f];
            for (int i = 0; i < loop.Length; i++)
            {
                int a = loop[i], b = loop[(i + 1) % loop.Length];
                var key = a < b ? (a, b) : (b, a);
                map[key] = map.TryGetValue(key, out var pair) ? (pair.Left, f) : (f, -1);
            }
        }

        return map.Select(e => (e.Key.Item1, e.Key.Item2, e.Value.Left, e.Value.Right)).ToList();
    }
}
