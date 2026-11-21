using System.Collections.Concurrent;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;
using Carbonado.Core.Persistence;

namespace Carbonado.Core.Watch;

/// <summary>
/// Watches a folder and raises <see cref="FileReady"/> once per new image after it has stopped changing
/// (size and timestamp stable for the debounce interval, and readable). Files inside the output sub-folder,
/// and paths registered with <see cref="Ignore"/>, are never reported — so outputs cannot re-trigger themselves.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _debounce;
    private readonly string? _ignoreDirectoryName;
    private readonly ConcurrentDictionary<string, Observation> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public FolderWatcher(WatchRule rule, TimeSpan? debounce = null, string? ignoreDirectoryName = "Carbonado")
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!Directory.Exists(rule.Folder))
        {
            throw new DirectoryNotFoundException(rule.Folder);
        }

        Rule = rule;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(750);
        _ignoreDirectoryName = ignoreDirectoryName;
        _watcher = new FileSystemWatcher(rule.Folder)
        {
            IncludeSubdirectories = rule.Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Observe(e.FullPath);
        _watcher.Changed += (_, e) => Observe(e.FullPath);
        _watcher.Renamed += (_, e) => Observe(e.FullPath);
        _watcher.Error += (_, e) => Log.Warn($"Watcher error on {rule.Folder}: {e.GetException().Message}");
        long period = Math.Max(50, (long)(_debounce.TotalMilliseconds / 3));
        _timer = new Timer(_ => Sweep(), null, period, period);
        _watcher.EnableRaisingEvents = true;
    }

    public WatchRule Rule { get; }

    /// <summary>Raised (on a thread-pool thread) with the full path of a new, stable image file.</summary>
    public event EventHandler<string>? FileReady;

    /// <summary>Never report <paramref name="path"/> (e.g. a file Carbonado itself wrote).</summary>
    public void Ignore(string path) => _ignored[Path.GetFullPath(path)] = 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void Observe(string path)
    {
        if (!FormatInfo.IsInputExtension(path) || _ignored.ContainsKey(path) || IsInIgnoredDirectory(path))
        {
            return;
        }

        _pending[path] = new Observation(DateTime.UtcNow, -1, DateTime.MinValue);
    }

    private bool IsInIgnoredDirectory(string path)
    {
        if (_ignoreDirectoryName is null)
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(path);
        string root = Rule.Folder.TrimEnd(Path.DirectorySeparatorChar);
        while (dir is not null && dir.Length > root.Length)
        {
            if (string.Equals(Path.GetFileName(dir), _ignoreDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (path, obs) in _pending)
        {
            if (now - obs.LastEvent < _debounce)
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    _pending.TryRemove(path, out _);
                    continue;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Stable = same size and timestamp as the previous sweep, non-empty, and openable for reading.
            if (info.Length == 0 || info.Length != obs.Size || info.LastWriteTimeUtc != obs.Stamp)
            {
                _pending[path] = obs with { LastEvent = now - _debounce, Size = info.Length, Stamp = info.LastWriteTimeUtc };
                continue;
            }

            if (!CanRead(path))
            {
                continue;
            }

            _pending.TryRemove(path, out _);
            if (_reported.TryGetValue(path, out var last) && now - last < TimeSpan.FromSeconds(10))
            {
                continue;
            }

            _reported[path] = now;
            FileReady?.Invoke(this, path);
        }

        foreach (var (path, at) in _reported)
        {
            if (now - at > TimeSpan.FromMinutes(1))
            {
                _reported.TryRemove(path, out _);
            }
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Observation(DateTime LastEvent, long Size, DateTime Stamp);
}
