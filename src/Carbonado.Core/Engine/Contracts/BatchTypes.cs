namespace Carbonado.Core.Engine;

public enum BatchState
{
    Idle,
    Running,
    Paused,
    Cancelling,
}

public sealed record BatchSummary(
    int Total,
    int Compressed,
    int Kept,
    int Failed,
    int Cancelled,
    long OriginalBytes,
    long OutputBytes,
    TimeSpan Elapsed)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - OutputBytes);

    public double SavedFraction => OriginalBytes > 0 ? (double)SavedBytes / OriginalBytes : 0;
}

/// <summary>Runs compression jobs in the background. Events are raised on thread-pool threads.</summary>
public interface IBatchProcessor : IAsyncDisposable
{
    BatchState State { get; }

    /// <summary>Jobs waiting to start.</summary>
    int Pending { get; }

    /// <summary>Jobs currently executing.</summary>
    int Running { get; }

    event EventHandler<JobProgress>? Progress;

    event EventHandler<CompressionResult>? JobCompleted;

    event EventHandler<BatchSummary>? BatchCompleted;

    event EventHandler<BatchState>? StateChanged;

    void Enqueue(CompressionJob job);

    /// <summary>Starts (or continues) processing queued jobs. Idempotent.</summary>
    void Start();

    void Pause();

    void Resume();

    void Cancel(Guid jobId);

    void CancelAll();
}
