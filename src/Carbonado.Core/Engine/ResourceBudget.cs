namespace Carbonado.Core.Engine;

/// <summary>
/// A two-dimensional counting semaphore over thread slots and bytes of memory, used by <see cref="BatchScheduler"/>
/// to bound how many jobs run at once and how much memory they may use together.
/// </summary>
/// <remarks>
/// Requests are granted strictly in arrival order (a large request at the head is never starved by smaller ones
/// behind it). A request larger than the whole budget is clamped to it, so such a job still runs, alone.
/// Thread-safe; leases may be released from any thread, and releasing twice is harmless.
/// </remarks>
public sealed class ResourceBudget
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _threads;
    private long _bytes;

    /// <param name="threads">Thread slots (at least 1).</param>
    /// <param name="memoryBytes">Bytes (at least 1).</param>
    public ResourceBudget(int threads, long memoryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBytes, 1);
        TotalThreads = threads;
        TotalBytes = memoryBytes;
        _threads = threads;
        _bytes = memoryBytes;
    }

    /// <summary>Thread slots in the budget.</summary>
    public int TotalThreads { get; }

    /// <summary>Bytes in the budget.</summary>
    public long TotalBytes { get; }

    /// <summary>Thread slots not leased right now.</summary>
    public int AvailableThreads
    {
        get
        {
            lock (_gate)
            {
                return _threads;
            }
        }
    }

    /// <summary>Bytes not leased right now.</summary>
    public long AvailableBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Requests waiting for resources.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="threads"/> slots and <paramref name="bytes"/> are free (after every earlier request),
    /// then leases them until the returned <see cref="Lease"/> is disposed. Values above the totals are clamped to them.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the lease was granted.</exception>
    public ValueTask<Lease> AcquireAsync(int threads, long bytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threads);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<Lease>(ct);
        }

        threads = Math.Min(threads, TotalThreads);
        bytes = Math.Min(bytes, TotalBytes);
        Waiter waiter;
        lock (_gate)
        {
            if (_waiters.Count == 0 && Fits(threads, bytes))
            {
                Take(threads, bytes);
                return ValueTask.FromResult(new Lease(this, threads, bytes));
            }

            waiter = new Waiter(threads, bytes);
            waiter.Node = _waiters.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(static state =>
            {
                var (budget, w, token) = ((ResourceBudget, Waiter, CancellationToken))state!;
                budget.Abandon(w, token);
            }, (this, waiter, ct));

            // Granted while registering: the registration is no longer needed.
            if (waiter.Completion.Task.IsCompleted)
            {
                waiter.Registration.Dispose();
            }
        }

        return new ValueTask<Lease>(waiter.Completion.Task);
    }

    private bool Fits(int threads, long bytes) => threads <= _threads && bytes <= _bytes;

    private void Take(int threads, long bytes)
    {
        _threads -= threads;
        _bytes -= bytes;
    }

    private void Release(int threads, long bytes)
    {
        List<Waiter> granted = [];
        lock (_gate)
        {
            _threads += threads;
            _bytes += bytes;
            GrantFromHead(granted);
        }

        Complete(granted);
    }

    // A cancelled waiter leaves the queue (unless it was granted already), which may unblock the ones behind it.
    private void Abandon(Waiter waiter, CancellationToken ct)
    {
        List<Waiter> granted = [];
        bool removed = false;
        lock (_gate)
        {
            if (waiter.Node?.List is not null)
            {
                _waiters.Remove(waiter.Node);
                waiter.Node = null;
                removed = true;
                GrantFromHead(granted);
            }
        }

        if (removed)
        {
            waiter.Completion.TrySetCanceled(ct);
        }

        Complete(granted);
    }

    private void GrantFromHead(List<Waiter> granted)
    {
        while (_waiters.First is { } node && Fits(node.Value.Threads, node.Value.Bytes))
        {
            Take(node.Value.Threads, node.Value.Bytes);
            _waiters.RemoveFirst();
            node.Value.Node = null;
            granted.Add(node.Value);
        }
    }

    // Outside the lock: continuations run asynchronously anyway, but registrations are disposed here.
    private void Complete(List<Waiter> granted)
    {
        foreach (Waiter w in granted)
        {
            w.Registration.Dispose();
            w.Completion.TrySetResult(new Lease(this, w.Threads, w.Bytes));
        }
    }

    /// <summary>Resources held until disposed (idempotent).</summary>
    public sealed class Lease : IDisposable
    {
        private ResourceBudget? _owner;

        internal Lease(ResourceBudget owner, int threads, long bytes)
        {
            _owner = owner;
            Threads = threads;
            Bytes = bytes;
        }

        /// <summary>Thread slots held (after clamping).</summary>
        public int Threads { get; }

        /// <summary>Bytes held (after clamping).</summary>
        public long Bytes { get; }

        /// <summary>Returns the resources to the budget (only the first call does).</summary>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(Threads, Bytes);
    }

    private sealed class Waiter(int threads, long bytes)
    {
        public int Threads { get; } = threads;

        public long Bytes { get; } = bytes;

        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }
}
