namespace Carbonado.Core.Engine;

/// <summary>What <see cref="BatchScheduler"/> needs from an engine (implemented by <see cref="CompressionEngine"/>; faked in tests).</summary>
internal interface IJobEngine
{
    /// <summary>Peak memory the job is expected to use, for the scheduler's memory budget.</summary>
    long EstimateMemoryBytes(CompressionJob job);

    /// <summary>Runs one job; never throws (cancellation ends as <see cref="JobOutcome.Cancelled"/>).</summary>
    Task<CompressionResult> RunAsync(CompressionJob job, IProgress<JobProgress>? progress, int threadBudget, CancellationToken ct);
}

/// <summary>A job cannot continue; the message is shown to the user as-is.</summary>
internal sealed class JobFailedException(string message) : Exception(message);

/// <summary>A candidate cannot continue (encoder error, verification failure); it is discarded, the job goes on.</summary>
internal sealed class CandidateFailedException(string message) : Exception(message);
