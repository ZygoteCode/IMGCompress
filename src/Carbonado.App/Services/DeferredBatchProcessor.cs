using Carbonado.Core.Engine;

namespace Carbonado.App.Services;

/// <summary>
/// An <see cref="IBatchProcessor"/> available from the first frame: calls made before the real engine is ready
/// (tool discovery, engine context) are recorded and replayed in order once <see cref="Attach"/> is called.
/// This keeps startup instant — the UI never waits for the engine.
/// </summary>
public sealed class DeferredBatchProcessor : IBatchProcessor
{
    private readonly Lock _gate = new();
    private readonly List<Operation> _pending = [];
    private IBatchProcessor? _inner;

    public event EventHandler<JobProgress>? Progress;

    public event EventHandler<CompressionResult>? JobCompleted;

    public event EventHandler<BatchSummary>? BatchCompleted;

    public event EventHandler<BatchState>? StateChanged;

    private enum OperationKind
    {
        Enqueue,
        Start,
        Pause,
        Resume,
        Cancel,
        CancelAll,
    }

    public bool IsAttached => Volatile.Read(ref _inner) is not null;

    public BatchState State
    {
        get
        {
            if (_inner is { } inner)
            {
                return inner.State;
            }

            lock (_gate)
            {
                return _pending.Any(o => o.Kind == OperationKind.Start) ? BatchState.Running : BatchState.Idle;
            }
        }
    }

    public int Pending
    {
        get
        {
            if (_inner is { } inner)
            {
                return inner.Pending;
            }

            lock (_gate)
            {
                return _pending.Count(o => o.Kind == OperationKind.Enqueue);
            }
        }
    }

    public int Running => _inner?.Running ?? 0;

    /// <summary>Connects the real processor and replays every call made so far, in order.</summary>
    public void Attach(IBatchProcessor inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        List<Operation> replay;
        lock (_gate)
        {
            if (_inner is not null)
            {
                throw new InvalidOperationException("Already attached.");
            }

            inner.Progress += (_, e) => Progress?.Invoke(this, e);
            inner.JobCompleted += (_, e) => JobCompleted?.Invoke(this, e);
            inner.BatchCompleted += (_, e) => BatchCompleted?.Invoke(this, e);
            inner.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
            replay = [.. _pending];
            _pending.Clear();
            Volatile.Write(ref _inner, inner);
        }

        foreach (var op in replay)
        {
            Execute(inner, op);
        }
    }

    public void Enqueue(CompressionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Submit(new Operation(OperationKind.Enqueue, job, job.Id));
    }

    public void Start() => Submit(new Operation(OperationKind.Start, null, null));

    public void Pause() => Submit(new Operation(OperationKind.Pause, null, null));

    public void Resume() => Submit(new Operation(OperationKind.Resume, null, null));

    public void Cancel(Guid jobId)
    {
        CompressionJob? dropped = null;
        lock (_gate)
        {
            if (_inner is null)
            {
                // Not handed to the engine yet: drop it and report it cancelled right away.
                int index = _pending.FindIndex(o => o.Kind == OperationKind.Enqueue && o.JobId == jobId);
                if (index >= 0)
                {
                    dropped = _pending[index].Job;
                    _pending.RemoveAt(index);
                }
            }
        }

        if (dropped is not null)
        {
            JobCompleted?.Invoke(this, CompressionResult.Cancel(dropped));
            return;
        }

        Submit(new Operation(OperationKind.Cancel, null, jobId));
    }

    public void CancelAll()
    {
        List<CompressionJob> dropped = [];
        lock (_gate)
        {
            if (_inner is null)
            {
                dropped = [.. _pending.Where(o => o.Kind == OperationKind.Enqueue).Select(o => o.Job!)];
                _pending.Clear();
            }
        }

        if (_inner is null)
        {
            foreach (var job in dropped)
            {
                JobCompleted?.Invoke(this, CompressionResult.Cancel(job));
            }

            if (dropped.Count > 0)
            {
                BatchCompleted?.Invoke(this, new BatchSummary(dropped.Count, 0, 0, 0, dropped.Count, 0, 0, TimeSpan.Zero));
            }

            return;
        }

        Submit(new Operation(OperationKind.CancelAll, null, null));
    }

    public ValueTask DisposeAsync() => _inner?.DisposeAsync() ?? ValueTask.CompletedTask;

    private void Submit(Operation op)
    {
        IBatchProcessor? inner;
        lock (_gate)
        {
            inner = _inner;
            if (inner is null)
            {
                _pending.Add(op);
                return;
            }
        }

        Execute(inner, op);
    }

    private static void Execute(IBatchProcessor target, Operation op)
    {
        switch (op.Kind)
        {
            case OperationKind.Enqueue:
                target.Enqueue(op.Job!);
                break;
            case OperationKind.Start:
                target.Start();
                break;
            case OperationKind.Pause:
                target.Pause();
                break;
            case OperationKind.Resume:
                target.Resume();
                break;
            case OperationKind.Cancel:
                target.Cancel(op.JobId!.Value);
                break;
            case OperationKind.CancelAll:
                target.CancelAll();
                break;
        }
    }

    private sealed record Operation(OperationKind Kind, CompressionJob? Job, Guid? JobId);
}
