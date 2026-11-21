using System.Windows.Media;
using System.Windows.Media.Media3D;
using Carbonado.UI.Brand;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The intro's immutable assets (brilliant-cut model and mesh, facet adjacency, wordmark geometry), built once per
/// process and frozen, so they are free-threaded. <see cref="PrepareInBackground"/> builds them on the thread pool
/// while the app starts, keeping their cost (geometry boolean ops, mesh build, first-use JIT) off the UI thread.
/// </summary>
internal static class IntroAssets
{
    /// <summary>Girdle segments of the intro diamond (the classic 57-facet brilliant).</summary>
    public const int Segments = 16;

    private static readonly Lazy<MeshGeometry3D> s_mesh = new(() => BrilliantCutMesh.Create(Segments), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<int[][]> s_vertexFacets = new(BuildVertexFacets, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Geometry> s_word = new(() => WordmarkGeometry.Compose(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static int s_preparing;

    /// <summary>The shared brilliant-cut model.</summary>
    public static BrilliantCut Cut => BrilliantCut.Build(Segments);

    /// <summary>The flat-shaded mesh of <see cref="Cut"/>.</summary>
    public static MeshGeometry3D Mesh => s_mesh.Value;

    /// <summary>For each vertex of <see cref="Cut"/>, the facets that contain it.</summary>
    public static int[][] VertexFacets => s_vertexFacets.Value;

    /// <summary>The whole wordmark at rest tracking.</summary>
    public static Geometry Word => s_word.Value;

    /// <summary>Starts building every asset on the thread pool (idempotent, returns immediately).</summary>
    public static void PrepareInBackground()
    {
        if (Interlocked.Exchange(ref s_preparing, 1) == 0)
        {
            _ = Task.Run(static () =>
            {
                try
                {
                    _ = Mesh;
                    _ = VertexFacets;
                    _ = WordmarkGeometry.Letters;
                    _ = Word;
                }
                catch (Exception)
                {
                    // Observed on purpose (never an unobserved task fault): the lazies cache the failure and rethrow it
                    // on the UI thread where the asset is needed, inside the intro's fail-safe frame.
                }
            });
        }
    }

    private static int[][] BuildVertexFacets()
    {
        BrilliantCut cut = Cut;
        var lists = new List<int>[cut.Vertices.Count];
        for (int v = 0; v < lists.Length; v++)
        {
            lists[v] = [];
        }

        for (int f = 0; f < cut.Facets.Count; f++)
        {
            foreach (int v in cut.Facets[f])
            {
                lists[v].Add(f);
            }
        }

        return [.. lists.Select(l => l.ToArray())];
    }
}
