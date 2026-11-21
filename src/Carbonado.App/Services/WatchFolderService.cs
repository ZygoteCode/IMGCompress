using Carbonado.Core.Engine;
using Carbonado.Core.Logging;
using Carbonado.Core.Persistence;
using Carbonado.Core.Watch;

namespace Carbonado.App.Services;

/// <summary>Runs one <see cref="FolderWatcher"/> per enabled watch rule and feeds new images to the batch.</summary>
public sealed class WatchFolderService : IDisposable
{
    private readonly IBatchProcessor _batch;
    private readonly IProfileCatalog _profiles;
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _ownJobs = [];
    private readonly Lock _gate = new();

    public WatchFolderService(IBatchProcessor batch, IProfileCatalog profiles)
    {
        _batch = batch;
        _profiles = profiles;
        _batch.JobCompleted += OnJobCompleted;
    }

    /// <summary>Raised when a watched file was processed (thread-pool thread).</summary>
    public event EventHandler<CompressionResult>? WatchedJobCompleted;

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>Starts/stops watchers to match <paramref name="rules"/>. Rules whose folder is missing are skipped.</summary>
    public void Apply(IEnumerable<WatchRule> rules)
    {
        var wanted = rules.Where(r => r.Enabled && Directory.Exists(r.Folder)).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var id in _watchers.Keys.Where(id => !wanted.ContainsKey(id) || wanted[id] != _watchers[id].Rule).ToList())
            {
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }

            foreach (var rule in wanted.Values.Where(r => !_watchers.ContainsKey(r.Id)))
            {
                try
                {
                    var watcher = new FolderWatcher(rule, ignoreDirectoryName: rule.Output.SubfolderName);
                    watcher.FileReady += (_, path) => OnFileReady(watcher, path);
                    _watchers[rule.Id] = watcher;
                }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    Log.Warn($"Cannot watch {rule.Folder}: {e.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        _batch.JobCompleted -= OnJobCompleted;
        lock (_gate)
        {
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void OnFileReady(FolderWatcher watcher, string path)
    {
        var profile = _profiles.Find(watcher.Rule.ProfileId) ?? _profiles.Active;
        var job = CompressionJob.Create(path, profile, watcher.Rule.Output, watcher.Rule.Folder);
        lock (_gate)
        {
            _ownJobs.Add(job.Id);
        }

        Log.Info($"Watch '{watcher.Rule.Folder}': queued {Path.GetFileName(path)}");
        _batch.Enqueue(job);
        _batch.Start();
    }

    private void OnJobCompleted(object? sender, CompressionResult result)
    {
        bool mine;
        lock (_gate)
        {
            mine = _ownJobs.Remove(result.JobId);
            if (mine && result.OutputPath is { } output)
            {
                foreach (var w in _watchers.Values)
                {
                    w.Ignore(output);
                }
            }
        }

        if (mine)
        {
            WatchedJobCompleted?.Invoke(this, result);
        }
    }
}
