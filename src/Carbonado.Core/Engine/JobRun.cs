using System.Collections.Concurrent;
using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;
using Carbonado.Core.Metrics;

namespace Carbonado.Core.Engine;

/// <summary>
/// The state of one running job shared by its candidates: the prepared source, the SSIMULACRA2 reference (built once,
/// scored concurrently), progress, candidate reports, and the decode-back verifications. Decoding an output and
/// measuring it is serialized per job, so at most one decoded candidate is in memory at a time (the scheduler's
/// estimate: source + reference cache + one candidate).
/// </summary>
internal sealed class JobRun : IAsyncDisposable
{
    private readonly IProgress<JobProgress>? _progress;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private readonly ConcurrentQueue<CandidateReport> _reports = new();
    private readonly Lock _referenceGate = new();
    private Task<Ssimulacra2Reference>? _reference;
    private int _probesDone;
    private int _probesTotal = 1;
    private int _fileCounter;

    public JobRun(EngineContext context, CompressionJob job, IProgress<JobProgress>? progress, string folder)
    {
        Context = context;
        Job = job;
        Folder = folder;
        _progress = progress;
        Tolerance = Math.Max(0, context.Options.ScoreTolerance);
    }

    public EngineContext Context { get; }

    public CompressionJob Job { get; }

    public CompressionProfile Profile => Job.Profile;

    /// <summary>The job's private temp folder (deleted by the engine when the job ends).</summary>
    public string Folder { get; }

    /// <summary>The prepared source; set before any candidate runs, owned (disposed) by this run.</summary>
    public PreparedSource Source { get; private set; } = null!;

    /// <summary>Frames in the original (> 1: animated).</summary>
    public int SourceFrames { get; private set; } = 1;

    public bool Animated => SourceFrames > 1;

    public double Tolerance { get; }

    /// <summary>Size of the original file.</summary>
    public long OriginalBytes { get; private set; }

    /// <summary>The candidates' race (threads and early stopping); set by <see cref="BeginEncoding"/>.</summary>
    public CandidateRace Race { get; private set; } = new(false, long.MaxValue, 1);

    public IReadOnlyList<CandidateReport> Reports => [.. _reports];

    /// <summary>Lossy searches stop after this many probes (Fast 3, Balanced 5, Max 7, Insane 9).</summary>
    public int MaxIterations => Profile.Effort switch
    {
        EffortLevel.Fast => 3,
        EffortLevel.Balanced => 5,
        EffortLevel.Max => 7,
        _ => 9,
    };

    public void Attach(PreparedSource source, int frames, long originalBytes)
    {
        Source = source;
        SourceFrames = Math.Max(1, frames);
        OriginalBytes = originalBytes;
    }

    public void Report(JobStage stage, double fraction, string? candidateId = null, double? score = null, long? bytes = null)
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            _progress.Report(new JobProgress(Job.Id, stage, Math.Clamp(fraction, 0, 1), null, candidateId, score, bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A faulty progress consumer must not fail the job.
            Log.Warn($"Progress handler failed for {Job.InputPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Enters the encoding stage, which advances by one step per finished probe out of <paramref name="expectedProbes"/>,
    /// and starts the candidates' race over <paramref name="threadBudget"/> encoder threads.
    /// </summary>
    public void BeginEncoding(int expectedProbes, int threadBudget)
    {
        _probesTotal = Math.Max(1, expectedProbes);
        long ceiling = Profile.NeverLarger ? OriginalBytes : long.MaxValue;
        Race = new CandidateRace(smallestWins: Profile.Mode != QualityMode.TargetSize, ceiling, threadBudget);
        Report(JobStage.Encoding, 0.20);
    }

    /// <summary>Records one encode attempt and reports it (Encoding stage, 0.20 → 0.90).</summary>
    public void ProbeFinished(CandidateReport report)
    {
        _reports.Enqueue(report);
        AdvanceEncoding(1, report.EncoderId, report.Score, report.Bytes > 0 ? report.Bytes : null);
    }

    /// <summary>A candidate ended before using all its expected probes (converged or stopped early): progress moves on.</summary>
    public void SkipProbes(int count)
    {
        if (count > 0)
        {
            AdvanceEncoding(count, null, null, null);
        }
    }

    /// <summary>A unique output path in the job folder for an attempt of <paramref name="plan"/>.</summary>
    public string NewCandidatePath(CandidatePlan plan)
    {
        int n = Interlocked.Increment(ref _fileCounter);
        string id = plan.Encoder.Id.Replace('.', '-');
        return Path.Combine(Folder, $"cand-{n:D3}-{id}-{plan.Kind.ToString().ToLowerInvariant()}{FormatInfo.Extension(plan.Format)}");
    }

    /// <summary>Starts building the SSIMULACRA2 reference in the background (while the first encodes run); once per job.</summary>
    public void StartReference()
    {
        lock (_referenceGate)
        {
            _reference ??= Task.Run(() => new Ssimulacra2Reference(Source.Pixels, Source.Metadata.Icc));
        }
    }

    /// <summary>Decodes an output and scores it against the source.</summary>
    /// <exception cref="CandidateFailedException">The output decodes to a different size.</exception>
    public async Task<double> ScoreAsync(string output, OutputFormat format, CancellationToken ct)
    {
        StartReference();
        Task<Ssimulacra2Reference> building;
        lock (_referenceGate)
        {
            building = _reference!;
        }

        Ssimulacra2Reference reference = await building.WaitAsync(ct).ConfigureAwait(false);
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using PixelBuffer decoded = await DecodeAsync(output, format, ct).ConfigureAwait(false);
            RequireSourceSize(decoded);
            return reference.Score(decoded, ct);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>Null when the output decodes to the source's visible pixels (all pixels when invisible RGB must be kept); else why not.</summary>
    public async Task<string?> VerifyPixelsAsync(string output, OutputFormat format, CancellationToken ct)
    {
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using PixelBuffer decoded = await DecodeAsync(output, format, ct).ConfigureAwait(false);
            if (decoded.Width != Source.Pixels.Width || decoded.Height != Source.Pixels.Height)
            {
                return $"decodes to {decoded.Width}×{decoded.Height} instead of {Source.Pixels.Width}×{Source.Pixels.Height}";
            }

            return PixelComparer.Identical(Source.Pixels, decoded, ignoreInvisibleRgb: Profile.CleanInvisiblePixels)
                ? null
                : "the decoded pixels differ from the source";
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>
    /// Null when an optimized JPEG decodes to the source pixels. The optimized file keeps the original orientation tag
    /// (its coefficients are not rotated), so the decoded output is turned upright before the comparison.
    /// </summary>
    public async Task<string?> VerifyJpegOptimizeAsync(string output, CancellationToken ct)
    {
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using PixelBuffer decoded = await DecodeAsync(output, OutputFormat.Jpeg, ct).ConfigureAwait(false);
            ushort orientation = ReadJpegOrientation(output);
            using PixelBuffer? upright = orientation == 1 ? null : Orientation.Apply(decoded, orientation);
            return PixelComparer.Identical(Source.Pixels, upright ?? decoded, ignoreInvisibleRgb: false)
                ? null
                : "the optimized JPEG decodes to different pixels";
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>
    /// Null when an animated output keeps every frame: same frame count and size as the source, and a first frame whose
    /// visible pixels equal the source's (the loader decodes the first frame of animations).
    /// </summary>
    public async Task<string?> VerifyAnimationAsync(string output, CancellationToken ct)
    {
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using DecodedImage decoded = await Context.Loader.LoadAsync(output, new LoadOptions(ApplyOrientation: false, FirstFrameOnly: true), ct).ConfigureAwait(false);
            if (decoded.FrameCount != SourceFrames)
            {
                return $"has {decoded.FrameCount} frame(s) instead of {SourceFrames}";
            }

            if (decoded.Pixels.Width != Source.Pixels.Width || decoded.Pixels.Height != Source.Pixels.Height)
            {
                return $"decodes to {decoded.Pixels.Width}×{decoded.Pixels.Height} instead of {Source.Pixels.Width}×{Source.Pixels.Height}";
            }

            return PixelComparer.Identical(Source.Pixels, decoded.Pixels, ignoreInvisibleRgb: true)
                ? null
                : "the first frame differs from the source";
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    /// <summary>
    /// Releases the reference and the prepared source (whose disposal waits for intermediates still being written).
    /// Called by the engine on the job's worker thread once every candidate has finished.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task<Ssimulacra2Reference>? pending;
        lock (_referenceGate)
        {
            pending = _reference;
        }

        if (pending is not null)
        {
            try
            {
                (await pending.ConfigureAwait(false)).Dispose();
            }
            catch (Exception)
            {
                // The reference could not be built; the candidates that needed it already reported the failure.
            }
        }

        Source?.Dispose();
        _decodeGate.Dispose();
    }

    private void AdvanceEncoding(int probes, string? candidateId, double? score, long? bytes)
    {
        int done = Interlocked.Add(ref _probesDone, probes);
        double fraction = 0.20 + (0.70 * Math.Min(1.0, done / (double)_probesTotal));
        Report(JobStage.Encoding, fraction, candidateId, score, bytes);
    }

    private async Task<PixelBuffer> DecodeAsync(string output, OutputFormat format, CancellationToken ct)
    {
        try
        {
            return await Context.Loader.DecodeOutputAsync(output, format, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CandidateFailedException($"the output cannot be decoded ({ex.Message})");
        }
    }

    private void RequireSourceSize(PixelBuffer decoded)
    {
        if (decoded.Width != Source.Pixels.Width || decoded.Height != Source.Pixels.Height)
        {
            throw new CandidateFailedException($"the output decodes to {decoded.Width}×{decoded.Height} instead of {Source.Pixels.Width}×{Source.Pixels.Height}");
        }
    }

    private static ushort ReadJpegOrientation(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return JpegSegments.Read(fs).Exif is { } exif ? ExifReader.ReadOrientation(exif) : (ushort)1;
        }
        catch (InvalidDataException)
        {
            return 1;
        }
    }
}
