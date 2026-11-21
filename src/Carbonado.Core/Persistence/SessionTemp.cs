using System.Diagnostics;
using System.Globalization;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Persistence;

/// <summary>
/// Private per-process temp folders under the data temp root: "&lt;pid&gt;-&lt;start ticks&gt;-&lt;random&gt;".
/// Folders of processes that are no longer alive (crash, kill) are wiped at the next start.
/// </summary>
public static class SessionTemp
{
    /// <summary>Creates this process's session folder.</summary>
    public static string Create(string tempRoot)
    {
        using var self = Process.GetCurrentProcess();
        string name = string.Create(CultureInfo.InvariantCulture,
            $"{self.Id}-{self.StartTime.ToUniversalTime().Ticks}-{Guid.NewGuid().ToString("N")[..6]}");
        string dir = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Deletes a session folder (best effort).</summary>
    public static void Delete(string sessionDir)
    {
        try
        {
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Temp cleanup incomplete for {sessionDir}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes session folders whose owning process is gone, and any folder older than <paramref name="olderThan"/>.
    /// Returns the number of folders removed.
    /// </summary>
    public static int CleanupStale(string tempRoot, TimeSpan olderThan)
    {
        if (!Directory.Exists(tempRoot))
        {
            return 0;
        }

        int removed = 0;
        foreach (var dir in new DirectoryInfo(tempRoot).EnumerateDirectories())
        {
            bool stale = DateTime.UtcNow - dir.LastWriteTimeUtc > olderThan || !IsOwnerAlive(dir.Name);
            if (!stale)
            {
                continue;
            }

            try
            {
                dir.Delete(recursive: true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not remove stale temp {dir.FullName}: {e.Message}");
            }
        }

        return removed;
    }

    /// <summary>True when the folder name encodes a still-running process (same PID and start time).</summary>
    internal static bool IsOwnerAlive(string folderName)
    {
        string[] parts = folderName.Split('-');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
