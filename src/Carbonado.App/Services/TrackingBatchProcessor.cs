using System.Collections.Concurrent;
using Carbonado.Core.Engine;
using Carbonado.Core.Persistence;

namespace Carbonado.App.Services;

/// <summary>
/// Decorates the batch processor: remembers which profile each job used and appends every finished job to the
/// history (from any source: Forge, watched folders, forwarded command lines).
/// </summary>
public sealed class TrackingBatchProcessor : IBatchProcessor
{
    private readonly IBatchProcessor _inner;
    private readonly HistoryStore _history;
    private readonly ConcurrentDictionary<Guid, string> _profiles = new();

    public TrackingBatchProcessor(IBatchProcessor inner, HistoryStore history)
    {
        _inner = inner;
        _history = history;
        _inner.JobCompleted += (_, r) =>
        {
            string profile = _profiles.TryRemove(r.JobId, out var id) ? id : "unknown";
            _history.Append(r, profile);
            JobCompleted?.Invoke(this, r);
        };
        _inner.Progress += (_, p) => Progress?.Invoke(this, p);
        _inner.BatchCompleted += (_, s) => BatchCompleted?.Invoke(this, s);
        _inner.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
    }

    public event EventHandler<JobProgress>? Progress;

    public event EventHandler<CompressionResult>? JobCompleted;

    public event EventHandler<BatchSummary>? BatchCompleted;

    public event EventHandler<BatchState>? StateChanged;

    public BatchState State => _inner.State;

    public int Pending => _inner.Pending;

    public int Running => _inner.Running;

    public void Enqueue(CompressionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _profiles[job.Id] = job.Profile.Id;
        _inner.Enqueue(job);
    }

    public void Start() => _inner.Start();

    public void Pause() => _inner.Pause();

    public void Resume() => _inner.Resume();

    public void Cancel(Guid jobId) => _inner.Cancel(jobId);

    public void CancelAll() => _inner.CancelAll();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
