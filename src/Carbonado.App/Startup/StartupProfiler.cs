using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Carbonado.App.Startup;

/// <summary>
/// Measures startup and shutdown milestones relative to process start (OS timestamp), so the numbers include
/// runtime boot. Results are shown in About and written to logs\startup-last.txt (read by the UI tests).
/// </summary>
public static class StartupProfiler
{
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static readonly List<(string Name, double Ms)> s_marks = [];
    private static readonly double s_offsetMs = ComputeOffset();

    /// <summary>Milliseconds since the process was created.</summary>
    public static double Now => s_offsetMs + s_clock.Elapsed.TotalMilliseconds;

    public static IReadOnlyList<(string Name, double Ms)> Marks
    {
        get
        {
            lock (s_marks)
            {
                return s_marks.ToArray();
            }
        }
    }

    /// <summary>Time to first rendered frame, or null before it happened.</summary>
    public static double? FirstFrameMs => Find("first-frame");

    /// <summary>Time until the shell was interactive (intro may still be playing).</summary>
    public static double? InteractiveMs => Find("interactive");

    public static void Mark(string name)
    {
        double now = Now;

        lock (s_marks)
        {
            s_marks.Add((name, now));
        }
    }

    public static double? Find(string name)
    {
        lock (s_marks)
        {
            foreach (var (n, ms) in s_marks)
            {
                if (n == name)
                {
                    return ms;
                }
            }
        }

        return null;
    }

    /// <summary>Writes "name=ms" lines to <paramref name="file"/> (best effort).</summary>
    public static void WriteTo(string file)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var (name, ms) in Marks)
            {
                sb.Append(name).Append('=').AppendLine(ms.ToString("F1", CultureInfo.InvariantCulture));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, sb.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double ComputeOffset()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return Math.Max(0, (DateTime.Now - p.StartTime).TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
