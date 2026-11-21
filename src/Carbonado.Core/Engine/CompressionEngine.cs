using System.Diagnostics;
using System.Globalization;
using Carbonado.Core.Analysis;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Turns one image into the smallest verified output its profile allows (design spec §4.4): probe → decode (upright,
/// once) → analyze (optional resize, invisible-pixel cleanup) → prepare (metadata policy, intermediates) → plan
/// candidates → encode them concurrently (lossy ones quality-searched) → verify every result → keep the smallest
/// (never larger than the original) → commit atomically → report.
/// </summary>
/// <remarks>
/// <para>Thread-safe: any number of jobs may run concurrently on one engine. Work runs on the thread pool, never on the
/// caller's thread.</para>
/// <para>Progress: Probing 0.02, Decoding 0.10, Analyzing 0.15, Preparing 0.20, Encoding 0.20–0.90 (one step per finished
/// probe, each carrying the candidate's id, score and size), Verifying 0.95, Writing 0.98, Done 1.0.</para>
/// <para>Results: <see cref="JobOutcome.KeptOriginal"/> and <see cref="JobOutcome.Skipped"/> carry a short note in
/// <see cref="CompressionResult.Error"/> explaining why; <see cref="JobOutcome.Failed"/> carries the reason (shown to the
/// user as-is). Lossy winners report their measured score; exact ones (pixel-identical, bit-exact) report none.</para>
/// </remarks>
public sealed class CompressionEngine : IJobEngine
{
    // Below this an estimate is noise; it also covers the tools' own working memory for tiny images.
    private const long MinimumJobBytes = 16L << 20;

    private readonly EngineContext _ctx;

    /// <summary>An engine running jobs in <paramref name="ctx"/> (tools, loader, encoders, session temp).</summary>
    public CompressionEngine(EngineContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
    }

    /// <summary>The context this engine runs in.</summary>
    public EngineContext Context => _ctx;

    /// <summary>
    /// Compresses one file. Never throws for processing problems: corrupt or unsupported inputs end as
    /// <see cref="JobOutcome.Failed"/> with a readable message; cancellation ends as <see cref="JobOutcome.Cancelled"/>
    /// with nothing written. The job's temp folder is deleted whatever happens.
    /// </summary>
    /// <param name="job">The file, profile and output plan.</param>
    /// <param name="progress">Receives stage and candidate updates (on thread-pool threads); may be null.</param>
    /// <param name="threadBudget">Encoder threads for this job, shared by its concurrent candidates (at least 1 each).</param>
    /// <param name="ct">Cancels the job; running encoders are killed.</param>
    public Task<CompressionResult> RunAsync(CompressionJob job, IProgress<JobProgress>? progress, int threadBudget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Task.Run(() => RunCoreAsync(job, progress, Math.Max(1, threadBudget), ct), CancellationToken.None);
    }

    /// <summary>
    /// The memory the job is expected to need at its peak, from the image header (no decoding): about 64 bytes per pixel
    /// for opaque images and 112 with transparency in lossy modes (pixels, the SSIMULACRA2 reference cache and one decoded
    /// candidate), 16 in lossless mode, +8 for 16-bit images, plus the full-size decode when the profile resizes.
    /// Unreadable headers are estimated from the file size.
    /// </summary>
    public long EstimateMemoryBytes(CompressionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ImageHeader.Info? header = ImageHeader.TryRead(job.InputPath);
        long pixels, prepared;
        bool alpha, deep;
        if (header is { } h)
        {
            pixels = h.PixelCount;
            alpha = h.MayHaveAlpha;
            deep = h.Deep;
            prepared = pixels;
            if (job.Profile.Resize is { } resize)
            {
                var (w, ht) = Resampler.FitWithin(h.Width, h.Height, resize.MaxWidth, resize.MaxHeight);
                prepared = (long)w * ht;
            }
        }
        else
        {
            long size = SafeLength(job.InputPath);
            pixels = prepared = Math.Clamp(size * 2, 1_000_000, 100_000_000);
            alpha = true;
            deep = false;
        }

        long perPixel = job.Profile.Mode == QualityMode.Lossless ? 16 : alpha ? 112 : 64;
        long estimate = prepared * (perPixel + (deep ? 8 : 0));
        if (prepared < pixels)
        {
            estimate += pixels * (deep ? 8 : 4) * 2;
        }

        return Math.Max(MinimumJobBytes, estimate);
    }

    private async Task<CompressionResult> RunCoreAsync(CompressionJob job, IProgress<JobProgress>? progress, int threadBudget, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long originalBytes = 0;
        string? folder = null;
        JobRun? run = null;
        try
        {
            Report(job, progress, JobStage.Probing, 0.02);
            ct.ThrowIfCancellationRequested();
            var (original, format, frames) = Inspect(job);
            originalBytes = original.Bytes;
            ValidateProfile(job.Profile);
            folder = _ctx.CreateJobFolder(job.Id);
            run = new JobRun(_ctx, job, progress, folder);

            CompressionResult result = await ProcessAsync(run, original, format, frames, threadBudget, clock, ct).ConfigureAwait(false);
            if (result.Outcome != JobOutcome.Cancelled)
            {
                run.Report(JobStage.Done, 1.0);
            }

            LogResult(result, clock.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            var cancelled = CompressionResult.Cancel(job, originalBytes, clock.Elapsed) with { Candidates = run?.Reports ?? [] };
            LogResult(cancelled, clock.Elapsed);
            return cancelled;
        }
        catch (JobFailedException ex)
        {
            return Fail(job, ex.Message, originalBytes, clock, run, progress);
        }
        catch (Exception ex)
        {
            Log.Error($"{job.InputPath}: unexpected engine failure.", ex);
            return Fail(job, "Unexpected error: " + ex.Message, originalBytes, clock, run, progress);
        }
        finally
        {
            if (run is not null)
            {
                await run.DisposeAsync().ConfigureAwait(false);
            }

            await DeleteFolderAsync(folder).ConfigureAwait(false);
        }
    }

    private async Task<CompressionResult> ProcessAsync(JobRun run, OutputWriter.OriginalFile original, ImageFormat format, int frames, int threadBudget, Stopwatch clock, CancellationToken ct)
    {
        CompressionJob job = run.Job;
        CompressionProfile profile = job.Profile;
        bool animated = frames > 1;

        run.Report(JobStage.Decoding, 0.10);
        DecodedImage decoded = await DecodeAsync(job.InputPath, ct).ConfigureAwait(false);
        int width, height;
        bool resized = false;
        ImageAnalysis analysis;
        try
        {
            frames = Math.Max(frames, decoded.FrameCount);
            animated = frames > 1;
            width = decoded.Pixels.Width;
            height = decoded.Pixels.Height;

            run.Report(JobStage.Analyzing, 0.15);
            if (profile.Resize is { } box && !animated)
            {
                var (w, h) = Resampler.FitWithin(width, height, box.MaxWidth, box.MaxHeight);
                if (w != width || h != height)
                {
                    decoded.ReplacePixels(Resampler.Resize(decoded.Pixels, w, h));
                    resized = true;
                }
            }

            // Animations are encoded from the original file; their first frame is only the verification reference.
            if (profile.CleanInvisiblePixels && !animated)
            {
                AlphaOps.CleanInvisible(decoded.Pixels);
            }

            analysis = ImageAnalyzer.Analyze(decoded.Pixels);
            ct.ThrowIfCancellationRequested();

            run.Report(JobStage.Preparing, 0.20);
            ImageMetadata metadata = MetadataProcessor.Apply(decoded.Metadata, profile.Metadata);
            PixelBuffer pixels = decoded.DetachPixels();
            try
            {
                run.Attach(PreparedSource.Create(job.InputPath, format, pixels, metadata, analysis, Path.Combine(run.Folder, "work"), frames, preserveInvisibleRgb: !profile.CleanInvisiblePixels), frames, original.Bytes);
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }
        finally
        {
            decoded.Dispose();
        }

        IReadOnlyList<CandidatePlan> plans = CandidatePlanner.Plan(profile, analysis, format, animated, _ctx.Encoders);
        if (resized)
        {
            // These keep the original JPEG's coefficients, hence its size.
            plans = plans.Where(p => p.Kind != EncodeKind.JpegTranscode && !(p.Kind == EncodeKind.Optimize && p.Format == OutputFormat.Jpeg)).ToList();
        }

        if (plans.Count == 0)
        {
            string note = animated
                ? "Animated image: no encoder that keeps every frame is allowed by the profile; the original was kept."
                : "No installed encoder can produce the profile's formats for this image; the original was kept.";
            return KeepOriginal(run, original, format, note, width, height, clock);
        }

        run.BeginEncoding(plans.Sum(p => p.Kind == EncodeKind.Lossy ? run.MaxIterations : 1), threadBudget);
        if (plans.Any(p => p.Kind == EncodeKind.Lossy))
        {
            run.StartReference();
        }

        CandidateResult[] results = await Task.WhenAll(plans.Select((p, i) => CandidateRunner.RunAsync(run, p, i, ct))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        run.Report(JobStage.Verifying, 0.95);
        CandidateResult? winner = Choose(results, profile);
        if (winner is null)
        {
            if (!results.Any(r => r.Produced))
            {
                throw new JobFailedException("Every encoder failed: " + (results.Select(r => r.Error).FirstOrDefault(e => e is not null) ?? "no output."));
            }

            string note = profile.Mode == QualityMode.TargetSize
                ? $"No candidate fits within {FormatBytes(profile.TargetBytes ?? 0)}; the original was kept."
                : profile.Mode == QualityMode.Lossless
                    ? "No candidate was verified smaller than the original; the original was kept."
                    : $"No candidate reached the quality target ({profile.TargetScore:0.#}) with fewer bytes than the original; the original was kept.";
            return KeepOriginal(run, original, format, note, width, height, clock);
        }

        if (profile.NeverLarger && winner.Bytes >= original.Bytes)
        {
            return KeepOriginal(run, original, format, $"The best verified candidate ({FormatInfo.DisplayName(winner.Format)}, {FormatBytes(winner.Bytes)}) is not smaller than the original; the original was kept.", width, height, clock);
        }

        ct.ThrowIfCancellationRequested();

        // Past this point the commit runs to completion: a half-placed output (or a moved original) is never left behind.
        run.Report(JobStage.Writing, 0.98);
        string? written = WriteOrFail(() => OutputWriter.WriteOutput(winner.Path!, job, original, winner.Format, winner.Score));
        var size = (run.Source.Pixels.Width, run.Source.Pixels.Height);
        return written is null
            ? new CompressionResult(job.Id, job.InputPath, null, JobOutcome.Skipped, original.Bytes, 0, winner.Format, winner.Plan.Encoder.Id, winner.Score, winner.Verification, clock.Elapsed, run.Reports,
                "An output with that name already exists (conflict policy: skip).", size.Width, size.Height)
            : new CompressionResult(job.Id, job.InputPath, written, JobOutcome.Compressed, original.Bytes, winner.Bytes, winner.Format, winner.Plan.Encoder.Id, winner.Score, winner.Verification, clock.Elapsed, run.Reports,
                null, size.Width, size.Height);
    }

    // Score mode: the smallest verified output. Size mode: the best score within the budget (exact outputs count as 100).
    private static CandidateResult? Choose(IEnumerable<CandidateResult> results, CompressionProfile profile)
    {
        IEnumerable<CandidateResult> verified = results.Where(r => r.Verified);
        return profile.Mode == QualityMode.TargetSize
            ? verified.Where(r => r.Bytes <= profile.TargetBytes).OrderByDescending(r => r.RankScore).ThenBy(r => r.Bytes).ThenBy(r => r.Order).FirstOrDefault()
            : verified.OrderBy(r => r.Bytes).ThenBy(r => r.Order).FirstOrDefault();
    }

    private static CompressionResult KeepOriginal(JobRun run, OutputWriter.OriginalFile original, ImageFormat format, string note, int width, int height, Stopwatch clock)
    {
        run.Report(JobStage.Writing, 0.98);
        string? copy = WriteOrFail(() => OutputWriter.CopyOriginal(run.Job, original));
        return new CompressionResult(run.Job.Id, run.Job.InputPath, copy, JobOutcome.KeptOriginal, original.Bytes, original.Bytes, FormatInfo.FromImageFormat(format), null, null, VerificationKind.None,
            clock.Elapsed, run.Reports, note, width, height);
    }

    // A destination that cannot be written (read-only folder, full disk, path too long…) fails this file only.
    private static string? WriteOrFail(Func<string?> write)
    {
        try
        {
            return write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new JobFailedException("The output cannot be written: " + ex.Message);
        }
    }

    // The source as it is now: size, timestamps, format (from content) and frame count.
    private static (OutputWriter.OriginalFile File, ImageFormat Format, int Frames) Inspect(CompressionJob job)
    {
        try
        {
            var info = new FileInfo(job.InputPath);
            if (!info.Exists)
            {
                throw new JobFailedException(Directory.Exists(job.InputPath) ? "This is a folder, not an image file." : "The file does not exist.");
            }

            if (info.Length == 0)
            {
                throw new JobFailedException("The file is empty.");
            }

            ImageFormat format = FormatSniffer.DetectFile(info.FullName);
            if (format == ImageFormat.Unknown)
            {
                throw new JobFailedException("The file is not a recognised image (unknown format, or the data is damaged).");
            }

            int frames = FormatSniffer.CountFrames(info.FullName, format);
            return (OutputWriter.OriginalFile.Of(info), format, frames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new JobFailedException(ex is UnauthorizedAccessException ? "Access to the file is denied." : "The file cannot be read: " + ex.Message);
        }
    }

    private async Task<DecodedImage> DecodeAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _ctx.Loader.LoadAsync(path, new LoadOptions(ApplyOrientation: true, FirstFrameOnly: true), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            throw new JobFailedException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JobFailedException("The file cannot be read: " + ex.Message);
        }
    }

    private static void ValidateProfile(CompressionProfile p)
    {
        if (p.Mode == QualityMode.TargetSize && p.TargetBytes is not > 0)
        {
            throw new JobFailedException($"The profile \"{p.Name}\" targets a size but has no size budget.");
        }

        if (p.Mode == QualityMode.TargetScore && !double.IsFinite(p.TargetScore))
        {
            throw new JobFailedException($"The profile \"{p.Name}\" has no valid quality target.");
        }
    }

    private static CompressionResult Fail(CompressionJob job, string message, long originalBytes, Stopwatch clock, JobRun? run, IProgress<JobProgress>? progress)
    {
        var failed = CompressionResult.Fail(job, message, originalBytes, clock.Elapsed) with { Candidates = run?.Reports ?? [] };
        Report(job, progress, JobStage.Done, 1.0);
        LogResult(failed, clock.Elapsed);
        return failed;
    }

    private static void Report(CompressionJob job, IProgress<JobProgress>? progress, JobStage stage, double fraction)
    {
        try
        {
            progress?.Report(new JobProgress(job.Id, stage, fraction));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Progress handler failed for {job.InputPath}: {ex.Message}");
        }
    }

    private static void LogResult(CompressionResult r, TimeSpan elapsed) =>
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"{r.InputPath}: {r.Outcome} {(r.Format is { } f ? FormatInfo.DisplayName(f) : "-")} {r.OriginalBytes} -> {r.OutputBytes} B, score {(r.Score is { } s ? s.ToString("0.00", CultureInfo.InvariantCulture) : "-")}, {r.Verification}, {elapsed.TotalSeconds:0.00} s{(r.Error is { } e ? " — " + e : string.Empty)}"));

    private static string FormatBytes(long bytes) => bytes >= 1 << 20
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.##} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private static async Task DeleteFolderAsync(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        // A killed tool can hold a handle for a moment after cancellation: retry briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    Log.Warn($"Could not delete the job folder {folder}: {ex.Message}");
                    return;
                }

                await Task.Delay(40 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }
}
