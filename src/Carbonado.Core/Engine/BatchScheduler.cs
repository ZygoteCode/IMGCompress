using System.Diagnostics;
using System.Threading.Channels;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Runs compression jobs in the background under a thread and memory budget: jobs start in queue order while a
/// worker slot (one per logical core, or <see cref="EngineOptions.MaxConcurrency"/>) and their memory estimate
/// (<see cref="CompressionEngine.EstimateMemoryBytes"/>, against <see cref="EngineOptions.MemoryBudgetFraction"/> of RAM)
/// are free. Many small files therefore run in parallel with single-threaded encoders, few large ones with
/// multi-threaded encoders: each job's thread budget is the core count divided by the number of jobs expected to run
/// alongside it (queue depth, worker slots and memory permitting).
/// </summary>
/// <remarks>
/// <para>Events are raised on a thread-pool thread, one at a time and in order (a job's progress before its
/// <see cref="JobCompleted"/>, every <see cref="JobCompleted"/> of a batch before its <see cref="BatchCompleted"/>); a
/// slow or throwing handler never blocks or breaks the engine. <see cref="Progress"/> is throttled per job to one event
/// per 50 ms, except stage changes, which are delivered at once.</para>
/// <para>States: <see cref="Start"/> runs the queue (Idle → Running), <see cref="Pause"/> stops starting new jobs while
/// the running ones finish, <see cref="Resume"/> continues, <see cref="CancelAll"/> cancels everything (Cancelling until
/// the running jobs have stopped, then Idle). Jobs enqueued while a batch runs join it. <see cref="BatchCompleted"/>
/// fires when the queue has drained, counting every job finished since the previous one.</para>
/// </remarks>
public sealed class BatchScheduler : IBatchProcessor
{
    /// <summary>Most one progress event per job per this interval (stage changes excepted).</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly IJobEngine _engine;
    private readonly ResourceBudget _budget;
    private readonly int _cores;
    private readonly Lock _gate = new();
    private readonly LinkedList<Entry> _queue = new();
    private readonly Dictionary<Guid, Entry> _running = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private Task? _dispatcher;
    private TaskCompletionSource? _drained;
    private Entry? _waiting;
    private BatchState _state = BatchState.Idle;
    private Tally _tally = new();
    private long _batchStarted;
    private bool _startAfterCancel;
    private bool _disposed;

    /// <summary>A scheduler for <paramref name="engine"/> with the budgets of <paramref name="options"/>.</summary>
    public BatchScheduler(CompressionEngine engine, EngineOptions options)
        : this(engine, options, Environment.ProcessorCount, MemoryBudget(options))
    {
    }

    /// <summary>For tests: any engine, explicit core count and memory budget.</summary>
    internal BatchScheduler(IJobEngine engine, EngineOptions options, int logicalCores, long memoryBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        _engine = engine;
        _cores = Math.Max(1, logicalCores);
        int workers = options.MaxConcurrency is { } n && n >= 1 ? n : _cores;
        _budget = new ResourceBudget(workers, Math.Max(1, memoryBudgetBytes));
        _pump = Task.Run(PumpAsync);
    }

    /// <inheritdoc/>
    public event EventHandler<JobProgress>? Progress;

    /// <inheritdoc/>
    public event EventHandler<CompressionResult>? JobCompleted;

    /// <inheritdoc/>
    public event EventHandler<BatchSummary>? BatchCompleted;

    /// <inheritdoc/>
    public event EventHandler<BatchState>? StateChanged;

    /// <inheritdoc/>
    public BatchState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Jobs waiting to start (queued, or waiting for their memory).</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count + (_waiting is null ? 0 : 1);
            }
        }
    }

    /// <summary>Jobs currently executing.</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>The budget jobs run under (worker slots and memory).</summary>
    public ResourceBudget Budget => _budget;

    /// <summary>Adds a job; it runs when the scheduler is (or becomes) running.</summary>
    /// <exception cref="ArgumentException">A job with the same id is already queued or running.</exception>
    /// <exception cref="ObjectDisposedException">The scheduler was disposed.</exception>
    public void Enqueue(CompressionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running.ContainsKey(job.Id) || _waiting?.Job.Id == job.Id || Find(job.Id) is not null)
            {
                throw new ArgumentException($"Job {job.Id} is already queued or running.", nameof(job));
            }

            _queue.AddLast(new Entry(this, job, _lifetime.Token));
        }

        _wake.Release();
    }

    /// <summary>Starts (or continues) processing queued jobs. Idempotent; while cancelling, the queue starts afterwards.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (_state)
            {
                case BatchState.Idle when _queue.Count > 0:
                    _batchStarted = Stopwatch.GetTimestamp();
                    SetState(BatchState.Running);
                    _dispatcher ??= Task.Run(DispatchAsync);
                    break;

                case BatchState.Paused:
                    SetState(BatchState.Running);
                    break;

                case BatchState.Cancelling:
                    _startAfterCancel = true;
                    break;
            }
        }

        _wake.Release();
    }

    /// <summary>Stops starting new jobs; running jobs finish.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_state == BatchState.Running)
            {
                SetState(BatchState.Paused);
            }
        }
    }

    /// <summary>Continues after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_state == BatchState.Paused)
            {
                SetState(BatchState.Running);
            }
        }

        _wake.Release();
    }

    /// <summary>Cancels one job: a queued one completes at once as cancelled, a running one stops (its tools are killed).</summary>
    public void Cancel(Guid jobId)
    {
        Entry? dropped = null;
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            if (Find(jobId) is { } node)
            {
                dropped = node.Value;
                _queue.Remove(node);
                Finish(CompressionResult.Cancel(dropped.Job));
            }
            else if (_waiting?.Job.Id == jobId)
            {
                stop = _waiting.Cts;
            }
            else if (_running.TryGetValue(jobId, out var running))
            {
                stop = running.Cts;
            }
        }

        // Outside the lock: cancellation callbacks may run engine continuations inline.
        TryCancel(stop);
        dropped?.Cts.Dispose();
    }

    /// <summary>Cancels every queued and running job; the state returns to Idle once the running ones have stopped.</summary>
    public void CancelAll()
    {
        List<Entry> dropped;
        List<CancellationTokenSource> stop;
        lock (_gate)
        {
            dropped = [.. _queue];
            _queue.Clear();
            stop = [.. _running.Values.Select(e => e.Cts)];
            if (_waiting is not null)
            {
                stop.Add(_waiting.Cts);
            }

            _startAfterCancel = false;
            if (stop.Count > 0)
            {
                SetState(BatchState.Cancelling);
            }

            foreach (Entry entry in dropped)
            {
                Finish(CompressionResult.Cancel(entry.Job));
            }
        }

        foreach (CancellationTokenSource cts in stop)
        {
            TryCancel(cts);
        }

        foreach (Entry entry in dropped)
        {
            entry.Cts.Dispose();
        }
    }

    /// <summary>Cancels everything and waits up to 2 s for running jobs to stop and pending events to be delivered.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // From here on nothing new is accepted or started.
            _disposed = true;
        }

        CancelAll();
        Task drained;
        lock (_gate)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_running.Count == 0 && _waiting is null)
            {
                _drained.TrySetResult();
            }

            drained = _drained.Task;
        }

        var deadline = Stopwatch.StartNew();
        try
        {
            await drained.WaitAsync(DisposeWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"BatchScheduler: {Running} job(s) still stopping after {DisposeWait.TotalSeconds:0} s.");
        }

        _lifetime.Cancel();
        _wake.Release();
        _events.Writer.TryComplete();
        TimeSpan left = DisposeWait - deadline.Elapsed;
        try
        {
            await _pump.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Handlers are still busy; the remaining events are dropped with the scheduler.
        }
    }

    // The default memory budget: a share of the physical memory available to the process.
    private static long MemoryBudget(EngineOptions options)
    {
        double fraction = options.MemoryBudgetFraction is > 0 and <= 1 ? options.MemoryBudgetFraction : 0.25;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0)
        {
            total = 8L << 30;
        }

        return Math.Max(256L << 20, (long)(total * fraction));
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished meanwhile.
        }
        catch (AggregateException ex)
        {
            Log.Error("A cancellation callback failed.", ex);
        }
    }

    // Starts jobs in queue order whenever the scheduler runs and their resources are free.
    private async Task DispatchAsync()
    {
        CancellationToken lifetime = _lifetime.Token;
        while (!lifetime.IsCancellationRequested)
        {
            Entry? next = null;
            lock (_gate)
            {
                if (_state == BatchState.Running && _waiting is null && _queue.First is { } head)
                {
                    next = head.Value;
                    _queue.RemoveFirst();
                    _waiting = next;
                }
            }

            if (next is null)
            {
                try
                {
                    await _wake.WaitAsync(lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await StartJobAsync(next).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let one job stop the dispatcher.
                Log.Error($"{next.Job.InputPath}: the job could not be started.", ex);
                lock (_gate)
                {
                    if (ReferenceEquals(_waiting, next))
                    {
                        _waiting = null;
                    }

                    if (!_running.ContainsKey(next.Job.Id))
                    {
                        Finish(CompressionResult.Fail(next.Job, "Unexpected error: " + ex.Message));
                    }
                }
            }
        }
    }

    private async Task StartJobAsync(Entry entry)
    {
        long bytes = Estimate(entry.Job);
        ResourceBudget.Lease lease;
        try
        {
            lease = await _budget.AcquireAsync(1, bytes, entry.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _waiting = null;
                Finish(CompressionResult.Cancel(entry.Job));
            }

            entry.Cts.Dispose();
            return;
        }

        int threadBudget = 0;
        bool cancelled = false;
        lock (_gate)
        {
            _waiting = null;

            // CancelAll sets Cancelling before it cancels the tokens: either way this job is over.
            if (entry.Cts.IsCancellationRequested || _state == BatchState.Cancelling)
            {
                cancelled = true;
                Finish(CompressionResult.Cancel(entry.Job));
            }
            else if (_state != BatchState.Running)
            {
                // Paused while waiting for resources: back to the head of the queue.
                _queue.AddFirst(entry);
            }
            else
            {
                _running.Add(entry.Job.Id, entry);
                threadBudget = ThreadBudget(lease.Bytes);
            }
        }

        if (threadBudget == 0)
        {
            lease.Dispose();
            if (cancelled)
            {
                entry.Cts.Dispose();
            }

            return;
        }

        _ = RunJobAsync(entry, lease, threadBudget);
    }

    // Cores shared by the jobs expected to run alongside this one: bounded by worker slots, by the jobs left, and by
    // how many jobs of this size fit in memory. Called under the lock, with the job already counted as running.
    private int ThreadBudget(long bytes)
    {
        long byQueue = _running.Count + _queue.Count;
        long byMemory = Math.Max(1, _budget.TotalBytes / Math.Max(1, bytes));
        long expected = Math.Clamp(Math.Min(Math.Min(_budget.TotalThreads, byQueue), byMemory), 1, _cores);
        return Math.Max(1, _cores / (int)expected);
    }

    private async Task RunJobAsync(Entry entry, ResourceBudget.Lease lease, int threadBudget)
    {
        CompressionResult result;
        try
        {
            // Never run engine code on the dispatcher's thread.
            await Task.Yield();
            result = await _engine.RunAsync(entry.Job, entry, threadBudget, entry.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CompressionResult.Cancel(entry.Job);
        }
        catch (Exception ex)
        {
            Log.Error($"{entry.Job.InputPath}: the engine failed unexpectedly.", ex);
            result = CompressionResult.Fail(entry.Job, "Unexpected error: " + ex.Message);
        }
        finally
        {
            lease.Dispose();
        }

        lock (_gate)
        {
            _running.Remove(entry.Job.Id);
            Finish(result);
        }

        entry.Cts.Dispose();
        _wake.Release();
    }

    private long Estimate(CompressionJob job)
    {
        try
        {
            return _engine.EstimateMemoryBytes(job);
        }
        catch (Exception ex)
        {
            Log.Warn($"{job.InputPath}: memory estimate failed ({ex.Message}); scheduling without one.");
            return 0;
        }
    }

    // Under the lock: counts a finished job, announces it, and closes the batch when nothing is left to do.
    private void Finish(CompressionResult result)
    {
        _tally.Add(result);
        Post(() => JobCompleted?.Invoke(this, result));
        if (_running.Count == 0 && _waiting is null)
        {
            _drained?.TrySetResult();
        }

        if (_running.Count > 0 || _waiting is not null || (_queue.Count > 0 && _state != BatchState.Cancelling))
        {
            return;
        }

        TimeSpan elapsed = _batchStarted == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_batchStarted);
        BatchSummary summary = _tally.ToSummary(elapsed);
        _tally = new Tally();
        BatchState next = _state == BatchState.Cancelling && _startAfterCancel && _queue.Count > 0 ? BatchState.Running : BatchState.Idle;
        _startAfterCancel = false;
        _batchStarted = next == BatchState.Running ? Stopwatch.GetTimestamp() : 0;
        Post(() => BatchCompleted?.Invoke(this, summary));
        SetState(next);
        if (next == BatchState.Running)
        {
            _wake.Release();
        }
    }

    // Under the lock.
    private void SetState(BatchState state)
    {
        if (_state != state)
        {
            _state = state;
            Post(() => StateChanged?.Invoke(this, state));
        }
    }

    private LinkedListNode<Entry>? Find(Guid id)
    {
        for (var node = _queue.First; node is not null; node = node.Next)
        {
            if (node.Value.Job.Id == id)
            {
                return node;
            }
        }

        return null;
    }

    private void Post(Action raise) => _events.Writer.TryWrite(raise);

    private async Task PumpAsync()
    {
        await foreach (Action raise in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Log.Error("A batch event handler failed.", ex);
            }
        }
    }

    /// <summary>A job known to the scheduler, and the throttle for its progress.</summary>
    private sealed class Entry(BatchScheduler owner, CompressionJob job, CancellationToken lifetime) : IProgress<JobProgress>
    {
        private readonly Lock _progressGate = new();
        private JobStage _lastStage = JobStage.Queued;
        private long _lastEmit;
        private double _lastFraction = -1;

        public CompressionJob Job { get; } = job;

        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        public void Report(JobProgress value)
        {
            lock (_progressGate)
            {
                long now = Stopwatch.GetTimestamp();
                if (value.Stage == _lastStage && (Stopwatch.GetElapsedTime(_lastEmit, now) < ProgressInterval || value.Fraction < _lastFraction))
                {
                    return;
                }

                _lastStage = value.Stage;
                _lastEmit = now;
                _lastFraction = value.Fraction;
                owner.Post(() => owner.Progress?.Invoke(owner, value));
            }
        }
    }

    /// <summary>The counts of the current batch.</summary>
    private sealed class Tally
    {
        private int _total, _compressed, _kept, _failed, _cancelled;
        private long _original, _output;

        public void Add(CompressionResult r)
        {
            _total++;
            switch (r.Outcome)
            {
                case JobOutcome.Compressed:
                    _compressed++;
                    _original += r.OriginalBytes;
                    _output += r.OutputBytes;
                    break;
                case JobOutcome.KeptOriginal:
                    _kept++;
                    _original += r.OriginalBytes;
                    _output += r.OutputBytes;
                    break;
                case JobOutcome.Skipped:
                    _kept++;
                    break;
                case JobOutcome.Failed:
                    _failed++;
                    break;
                default:
                    _cancelled++;
                    break;
            }
        }

        public BatchSummary ToSummary(TimeSpan elapsed) => new(_total, _compressed, _kept, _failed, _cancelled, _original, _output, elapsed);
    }
}
