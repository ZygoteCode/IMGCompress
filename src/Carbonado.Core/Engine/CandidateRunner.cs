using System.Diagnostics;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Runs one planned candidate to a verified output: lossy kinds are quality-searched (every probe decoded and
/// scored), exact kinds are encoded once and proven (pixel-identical, bit-exact JPEG reconstruction, or every frame of
/// an animation kept). Candidates race (<see cref="CandidateRace"/>): each verified size is offered to the others, and
/// a candidate that can no longer win is stopped early. A failing candidate is logged and discarded; only the job's
/// cancellation propagates.
/// </summary>
internal static class CandidateRunner
{
    public static async Task<CandidateResult> RunAsync(JobRun run, CandidatePlan plan, int order, CancellationToken ct)
    {
        var attempt = new Attempt();
        using CandidateRace.Entrant entrant = run.Race.Join(plan, run.OriginalBytes, ct);
        int expected = plan.Kind == EncodeKind.Lossy ? run.MaxIterations : 1;
        try
        {
            entrant.Token.ThrowIfCancellationRequested();
            return plan.Kind switch
            {
                EncodeKind.Lossy => await LossyAsync(run, plan, order, entrant, attempt).ConfigureAwait(false),
                EncodeKind.JpegTranscode => await TranscodeAsync(run, plan, order, entrant, attempt).ConfigureAwait(false),
                _ => await ExactAsync(run, plan, order, entrant, attempt).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (entrant.AbandonReason is { } reason && !ct.IsCancellationRequested)
        {
            // Another candidate already won by more than this one could: not a failure.
            Log.Info($"{run.Job.InputPath}: candidate {plan} {reason}.");
            return CandidateResult.Discarded(plan, order, reason, produced: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected (a tool that cannot start, an unreadable output…): this candidate only.
            string reason = ex is CandidateFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            return Discard(run, plan, order, reason, attempt.Produced);
        }
        finally
        {
            run.SkipProbes(expected - attempt.Probes);
        }
    }

    /// <summary>
    /// The score whose initial guess fits a byte budget: from the budget's bits per pixel (≈ 80 at 1 bpp, ±10 per
    /// doubling), clamped to 35–95. Only seeds the size search.
    /// </summary>
    internal static double ScoreForBudget(long maxBytes, long pixels)
    {
        double bitsPerPixel = maxBytes * 8.0 / Math.Max(1, pixels);
        return Math.Clamp(80 + (10 * Math.Log2(Math.Max(bitsPerPixel, 1e-6))), 35, 95);
    }

    private static async Task<CandidateResult> LossyAsync(JobRun run, CandidatePlan plan, int order, CandidateRace.Entrant entrant, Attempt attempt)
    {
        IEncoderAdapter adapter = plan.Encoder;
        CompressionProfile profile = run.Profile;
        PreparedSource source = run.Source;
        bool sizeMode = profile.Mode == QualityMode.TargetSize;
        long budget = profile.TargetBytes ?? long.MaxValue;
        double target = profile.TargetScore;

        async Task<Probe> EvaluateAsync(double parameter, CancellationToken token)
        {
            attempt.Probes++;
            string output = run.NewCandidatePath(plan);
            var clock = Stopwatch.StartNew();
            var request = new EncodeRequest(source, EncodeKind.Lossy, parameter, profile.Effort, plan.Chroma, run.Race.ThreadsPerEncode, output);
            EncodeOutcome outcome = await adapter.EncodeAsync(request, token).ConfigureAwait(false);
            if (!outcome.Success)
            {
                run.ProbeFinished(new CandidateReport(adapter.Id, plan.Format, EncodeKind.Lossy, parameter, 0, null, false, clock.Elapsed));
                throw new CandidateFailedException(outcome.Error ?? $"{adapter.Id} failed.");
            }

            attempt.Produced = true;
            double score;
            try
            {
                score = await run.ScoreAsync(output, plan.Format, token).ConfigureAwait(false);
            }
            catch (CandidateFailedException)
            {
                run.ProbeFinished(new CandidateReport(adapter.Id, plan.Format, EncodeKind.Lossy, parameter, outcome.Bytes, null, false, clock.Elapsed));
                throw;
            }

            bool passed = sizeMode ? outcome.Bytes <= budget : score >= target;
            run.ProbeFinished(new CandidateReport(adapter.Id, plan.Format, EncodeKind.Lossy, parameter, outcome.Bytes, score, passed, clock.Elapsed));
            if (!sizeMode)
            {
                // Bytes grow with quality: a pass bounds every other candidate, a failure bounds this one.
                if (passed)
                {
                    run.Race.Offer(outcome.Bytes);
                }
                else
                {
                    entrant.RaiseFloor(outcome.Bytes);
                }
            }

            return new Probe(parameter, outcome.Bytes, score, output);
        }

        SearchResult result;
        try
        {
            result = sizeMode
                ? await QualitySearch.ForSizeAsync(EvaluateAsync, adapter.Scale, budget, adapter.InitialGuess(ScoreForBudget(budget, source.Pixels.PixelCount), source.Analysis), run.MaxIterations, entrant.Token).ConfigureAwait(false)
                : await QualitySearch.ForScoreAsync(EvaluateAsync, adapter.Scale, target, adapter.InitialGuess(target, source.Analysis), run.MaxIterations, run.Tolerance, entrant.Token).ConfigureAwait(false);
        }
        catch (CandidateFailedException ex)
        {
            return Discard(run, plan, order, ex.Message, attempt.Produced);
        }

        foreach (Probe probe in result.Probes.Where(p => !ReferenceEquals(p, result.Best)))
        {
            AtomicFile.TryDelete(probe.Path);
        }

        if (result.Best is not { } best)
        {
            string reason = sizeMode
                ? $"no quality fits {budget} bytes"
                : $"never reached {target:0.#} (best {result.Probes.Max(p => p.Score):0.0})";
            return Discard(run, plan, order, reason, attempt.Produced);
        }

        return new CandidateResult(plan, order, best.Path, best.Bytes, best.Score, best.Parameter, VerificationKind.PerceptualScore, null, true);
    }

    private static async Task<CandidateResult> ExactAsync(JobRun run, CandidatePlan plan, int order, CandidateRace.Entrant entrant, Attempt attempt)
    {
        CancellationToken ct = entrant.Token;
        attempt.Probes++;
        var clock = Stopwatch.StartNew();
        string output = run.NewCandidatePath(plan);
        var request = new EncodeRequest(run.Source, plan.Kind, null, run.Profile.Effort, plan.Chroma, run.Race.ThreadsPerEncode, output);
        EncodeOutcome outcome = await plan.Encoder.EncodeAsync(request, ct).ConfigureAwait(false);
        if (!outcome.Success)
        {
            run.ProbeFinished(new CandidateReport(plan.Encoder.Id, plan.Format, plan.Kind, null, 0, null, false, clock.Elapsed));
            return Discard(run, plan, order, outcome.Error ?? $"{plan.Encoder.Id} failed.", produced: false);
        }

        attempt.Produced = true;
        string? mismatch = run.Animated
            ? await run.VerifyAnimationAsync(output, ct).ConfigureAwait(false)
            : plan.Kind == EncodeKind.Optimize && plan.Format == OutputFormat.Jpeg
                ? await run.VerifyJpegOptimizeAsync(output, ct).ConfigureAwait(false)
                : await run.VerifyPixelsAsync(output, plan.Format, ct).ConfigureAwait(false);
        run.ProbeFinished(new CandidateReport(plan.Encoder.Id, plan.Format, plan.Kind, null, outcome.Bytes, null, mismatch is null, clock.Elapsed));
        if (mismatch is not null)
        {
            AtomicFile.TryDelete(output);
            return Discard(run, plan, order, "verification failed: " + mismatch, produced: true);
        }

        run.Race.Offer(outcome.Bytes);
        return new CandidateResult(plan, order, output, outcome.Bytes, null, null, VerificationKind.PixelExact, null, true);
    }

    private static async Task<CandidateResult> TranscodeAsync(JobRun run, CandidatePlan plan, int order, CandidateRace.Entrant entrant, Attempt attempt)
    {
        CancellationToken ct = entrant.Token;
        attempt.Probes++;
        var clock = Stopwatch.StartNew();
        string output = run.NewCandidatePath(plan);
        var request = new EncodeRequest(run.Source, EncodeKind.JpegTranscode, null, run.Profile.Effort, plan.Chroma, run.Race.ThreadsPerEncode, output);
        EncodeOutcome outcome = await plan.Encoder.EncodeAsync(request, ct).ConfigureAwait(false);
        if (!outcome.Success)
        {
            run.ProbeFinished(new CandidateReport(plan.Encoder.Id, plan.Format, plan.Kind, null, 0, null, false, clock.Elapsed));
            return Discard(run, plan, order, outcome.Error ?? $"{plan.Encoder.Id} failed.", produced: false);
        }

        attempt.Produced = true;

        // The reference is the JPEG the transcode was made from: the original, or its policy-filtered copy.
        string reference = await run.Source.GetJpegReferenceAsync(ct).ConfigureAwait(false);
        bool exact = await run.Context.Verifier.VerifyJpegReconstructionAsync(output, reference, ct).ConfigureAwait(false);
        run.ProbeFinished(new CandidateReport(plan.Encoder.Id, plan.Format, plan.Kind, null, outcome.Bytes, null, exact, clock.Elapsed));
        if (!exact)
        {
            AtomicFile.TryDelete(output);
            return Discard(run, plan, order, "verification failed: the JPEG is not reconstructed bit for bit", produced: true);
        }

        run.Race.Offer(outcome.Bytes);
        return new CandidateResult(plan, order, output, outcome.Bytes, null, null, VerificationKind.BitExactReconstruction, null, true);
    }

    private static CandidateResult Discard(JobRun run, CandidatePlan plan, int order, string reason, bool produced)
    {
        Log.Warn($"{run.Job.InputPath}: candidate {plan} discarded: {reason}");
        return CandidateResult.Discarded(plan, order, reason, produced);
    }

    /// <summary>What a candidate did so far: probes started, and whether any encode succeeded.</summary>
    private sealed class Attempt
    {
        public int Probes { get; set; }

        public bool Produced { get; set; }
    }
}
