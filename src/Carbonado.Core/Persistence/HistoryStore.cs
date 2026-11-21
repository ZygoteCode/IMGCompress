using System.Text;
using System.Text.Json;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Persistence;

/// <summary>
/// Append-only compression history (JSON lines) plus an aggregate stats file so lifetime totals load in O(1).
/// Thread-safe. Cancelled and skipped jobs are not recorded.
/// </summary>
public sealed class HistoryStore
{
    private readonly DataPaths _paths;
    private readonly Lock _gate = new();
    private StatsSnapshot? _stats;

    public HistoryStore(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Raised after an entry is appended.</summary>
    public event EventHandler<HistoryEntry>? Appended;

    public void Append(CompressionResult result, string profileId)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome is JobOutcome.Cancelled or JobOutcome.Skipped)
        {
            return;
        }

        var entry = new HistoryEntry(
            DateTimeOffset.Now,
            result.InputPath,
            result.OutputPath,
            result.Outcome,
            result.OriginalBytes,
            result.Outcome == JobOutcome.Failed ? 0 : result.OutputBytes,
            result.Format,
            result.Score,
            profileId,
            Math.Round(result.Elapsed.TotalSeconds, 3));

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_paths.LocalRoot);

                // Load (or rebuild) the aggregate BEFORE appending, so a rebuild cannot count this entry twice.
                var stats = LoadStatsUnlocked();
                string line = JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + "\n";
                using (var fs = new FileStream(_paths.HistoryFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(Encoding.UTF8.GetBytes(line));
                }

                Accumulate(stats, entry);
                SaveStatsUnlocked(stats);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not append history.", e);
                return;
            }
        }

        Appended?.Invoke(this, entry);
    }

    /// <summary>The newest <paramref name="max"/> entries, newest first. Reads the file backwards in blocks.</summary>
    public IReadOnlyList<HistoryEntry> ReadRecent(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        lock (_gate)
        {
            if (!File.Exists(_paths.HistoryFile))
            {
                return [];
            }

            var result = new List<HistoryEntry>(Math.Min(max, 1024));
            foreach (string line in ReadLinesBackwards(_paths.HistoryFile))
            {
                if (TryParse(line, out var entry))
                {
                    result.Add(entry);
                    if (result.Count == max)
                    {
                        break;
                    }
                }
            }

            return result;
        }
    }

    public LifetimeStats Stats()
    {
        lock (_gate)
        {
            var s = LoadStatsUnlocked();
            var byFormat = new Dictionary<OutputFormat, int>();
            foreach (var (key, count) in s.ByFormat)
            {
                if (Enum.TryParse<OutputFormat>(key, ignoreCase: true, out var f))
                {
                    byFormat[f] = count;
                }
            }

            return new LifetimeStats(s.Files, s.OriginalBytes, s.OutputBytes, byFormat);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            TryDelete(_paths.HistoryFile);
            TryDelete(_paths.StatsFile);
            _stats = new StatsSnapshot();
        }
    }

    private static void Accumulate(StatsSnapshot stats, HistoryEntry e)
    {
        if (e.Outcome is not (JobOutcome.Compressed or JobOutcome.KeptOriginal))
        {
            return;
        }

        stats.Files++;
        stats.OriginalBytes += e.OriginalBytes;
        stats.OutputBytes += e.Outcome == JobOutcome.Compressed ? e.OutputBytes : e.OriginalBytes;
        if (e.Format is { } f && e.Outcome == JobOutcome.Compressed)
        {
            string key = f.ToString();
            stats.ByFormat[key] = stats.ByFormat.GetValueOrDefault(key) + 1;
        }
    }

    private StatsSnapshot LoadStatsUnlocked()
    {
        if (_stats is not null)
        {
            return _stats;
        }

        try
        {
            if (File.Exists(_paths.StatsFile))
            {
                using var fs = File.OpenRead(_paths.StatsFile);
                _stats = JsonSerializer.Deserialize(fs, HistoryJsonContext.Default.StatsSnapshot);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Stats unreadable ({e.Message}); rebuilding from history.");
            _stats = null;
        }

        _stats ??= Rebuild();
        _stats.ByFormat ??= [];
        return _stats;
    }

    private StatsSnapshot Rebuild()
    {
        var stats = new StatsSnapshot();
        if (File.Exists(_paths.HistoryFile))
        {
            foreach (string line in File.ReadLines(_paths.HistoryFile))
            {
                if (TryParse(line, out var e))
                {
                    Accumulate(stats, e);
                }
            }
        }

        return stats;
    }

    private void SaveStatsUnlocked(StatsSnapshot stats)
    {
        string temp = _paths.StatsFile + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, stats, HistoryJsonContext.Default.StatsSnapshot);
        }

        File.Move(temp, _paths.StatsFile, overwrite: true);
    }

    private static bool TryParse(string line, out HistoryEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry);
            if (parsed is null)
            {
                return false;
            }

            entry = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadLinesBackwards(string path)
    {
        const int block = 64 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long position = fs.Length;
        var carry = new List<byte>();
        var buffer = new byte[block];
        while (position > 0)
        {
            int read = (int)Math.Min(block, position);
            position -= read;
            fs.Position = position;
            fs.ReadExactly(buffer, 0, read);
            for (int i = read - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    if (carry.Count > 0)
                    {
                        carry.Reverse();
                        yield return Encoding.UTF8.GetString(carry.ToArray());
                        carry.Clear();
                    }
                }
                else
                {
                    carry.Add(buffer[i]);
                }
            }
        }

        if (carry.Count > 0)
        {
            carry.Reverse();
            yield return Encoding.UTF8.GetString(carry.ToArray());
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {path}: {e.Message}");
        }
    }
}
