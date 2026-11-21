using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

public enum JobStage
{
    Queued,
    Probing,
    Decoding,
    Analyzing,
    Preparing,
    Encoding,
    Verifying,
    Writing,
    Done,
}

public enum JobOutcome
{
    /// <summary>A smaller verified output was written.</summary>
    Compressed,

    /// <summary>No candidate beat the original; the original was kept (copied when the plan writes elsewhere).</summary>
    KeptOriginal,

    /// <summary>Not processed (e.g. output exists and the conflict policy is Skip).</summary>
    Skipped,

    Failed,
    Cancelled,
}

public enum VerificationKind
{
    None,

    /// <summary>SSIMULACRA2 measured against the source pixels and found ≥ target.</summary>
    PerceptualScore,

    /// <summary>Decoded output pixels identical to the source (visible pixels).</summary>
    PixelExact,

    /// <summary>Original file reconstructed bit-for-bit from the output (JPEG → JXL).</summary>
    BitExactReconstruction,
}

/// <param name="Fraction">0..1 progress of this job.</param>
public sealed record JobProgress(
    Guid JobId,
    JobStage Stage,
    double Fraction,
    string? Detail = null,
    string? CandidateId = null,
    double? CandidateScore = null,
    long? CandidateBytes = null);

/// <summary>One encode attempt considered during a job.</summary>
public sealed record CandidateReport(
    string EncoderId,
    OutputFormat Format,
    EncodeKind Kind,
    double? Parameter,
    long Bytes,
    double? Score,
    bool Passed,
    TimeSpan Elapsed);

public sealed record CompressionJob(Guid Id, string InputPath, string? InputRoot, CompressionProfile Profile, OutputPlan Output)
{
    public static CompressionJob Create(string inputPath, CompressionProfile profile, OutputPlan output, string? inputRoot = null) =>
        new(Guid.NewGuid(), inputPath, inputRoot, profile, output);
}

public sealed record CompressionResult(
    Guid JobId,
    string InputPath,
    string? OutputPath,
    JobOutcome Outcome,
    long OriginalBytes,
    long OutputBytes,
    OutputFormat? Format,
    string? EncoderId,
    double? Score,
    VerificationKind Verification,
    TimeSpan Elapsed,
    IReadOnlyList<CandidateReport> Candidates,
    string? Error,
    int Width,
    int Height)
{
    /// <summary>1 − output/original (0 when nothing was saved).</summary>
    public double SavedFraction =>
        OriginalBytes > 0 && OutputBytes > 0 && OutputBytes < OriginalBytes ? 1.0 - ((double)OutputBytes / OriginalBytes) : 0.0;

    public long SavedBytes => OriginalBytes > 0 && OutputBytes > 0 ? Math.Max(0, OriginalBytes - OutputBytes) : 0;

    public static CompressionResult Fail(CompressionJob job, string error, long originalBytes = 0, TimeSpan elapsed = default) =>
        new(job.Id, job.InputPath, null, JobOutcome.Failed, originalBytes, 0, null, null, null, VerificationKind.None,
            elapsed, [], error, 0, 0);

    public static CompressionResult Cancel(CompressionJob job, long originalBytes = 0, TimeSpan elapsed = default) =>
        new(job.Id, job.InputPath, null, JobOutcome.Cancelled, originalBytes, 0, null, null, null, VerificationKind.None,
            elapsed, [], null, 0, 0);
}
