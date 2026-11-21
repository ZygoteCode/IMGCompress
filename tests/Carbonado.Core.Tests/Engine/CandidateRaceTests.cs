using Carbonado.Core.Analysis;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Engine;

public class CandidateRaceTests
{
    private static readonly CandidatePlan Lossy = new(new StubAdapter(OutputFormat.Avif), EncodeKind.Lossy, ChromaMode.Auto);
    private static readonly CandidatePlan Lossless = new(new StubAdapter(OutputFormat.Png), EncodeKind.Lossless, ChromaMode.Auto);
    private static readonly CandidatePlan Transcode = new(new StubAdapter(OutputFormat.Jxl), EncodeKind.JpegTranscode, ChromaMode.Auto);
    private static readonly CandidatePlan JpegOptimize = new(new StubAdapter(OutputFormat.Jpeg), EncodeKind.Optimize, ChromaMode.Auto);

    [Fact]
    public void Race_StopsALossyCandidateWhoseFailingProbeIsAlreadyTooLarge()
    {
        var race = new CandidateRace(smallestWins: true, long.MaxValue, threads: 8);
        using var webp = race.Join(Lossy, 1_000_000, CancellationToken.None);
        using var avif = race.Join(Lossy, 1_000_000, CancellationToken.None);

        webp.RaiseFloor(300_000);      // a failing probe of 300 KB: a pass needs more
        race.Offer(120_000);           // another candidate passed with 120 KB

        Assert.True(webp.Token.IsCancellationRequested);
        Assert.Contains("120000 bytes", webp.AbandonReason);
        Assert.False(avif.Token.IsCancellationRequested);
        Assert.Null(avif.AbandonReason);
        Assert.Equal(120_000, race.Best);
    }

    [Fact]
    public void Race_FloorsOfExactJpegPathsAndTheOriginalCeiling()
    {
        var race = new CandidateRace(smallestWins: true, ceiling: 1_000_000, threads: 4);
        using var transcode = race.Join(Transcode, 1_000_000, CancellationToken.None);
        using var optimize = race.Join(JpegOptimize, 1_000_000, CancellationToken.None);
        using var lossless = race.Join(Lossless, 1_000_000, CancellationToken.None);
        using var lossy = race.Join(Lossy, 1_000_000, CancellationToken.None);

        // A lossy candidate failing above the original can never be kept (never larger).
        lossy.RaiseFloor(1_200_000);
        Assert.True(lossy.Token.IsCancellationRequested);

        race.Offer(550_000);
        Assert.False(transcode.Token.IsCancellationRequested); // 50 % floor: 500 000 < 550 000
        Assert.True(optimize.Token.IsCancellationRequested);   // 60 % floor: 600 000 ≥ 550 000

        race.Offer(100_000);
        Assert.True(transcode.Token.IsCancellationRequested);
        Assert.False(lossless.Token.IsCancellationRequested);  // no floor: never stopped

        // A larger offer never raises the best.
        race.Offer(700_000);
        Assert.Equal(100_000, race.Best);
    }

    [Fact]
    public void Race_InSizeModeNothingIsStopped()
    {
        var race = new CandidateRace(smallestWins: false, long.MaxValue, threads: 4);
        using var a = race.Join(Lossy, 1000, CancellationToken.None);
        a.RaiseFloor(5000);

        race.Offer(10);

        Assert.False(a.Token.IsCancellationRequested);
    }

    [Fact]
    public void Race_SharesThreadsAmongTheCandidatesStillRunning()
    {
        var race = new CandidateRace(true, long.MaxValue, threads: 12);
        var entrants = Enumerable.Range(0, 6).Select(_ => race.Join(Lossless, 1, CancellationToken.None)).ToList();
        Assert.Equal(2, race.ThreadsPerEncode);

        entrants.Take(5).ToList().ForEach(e => e.Dispose());
        Assert.Equal(12, race.ThreadsPerEncode);

        entrants[5].Dispose();
        entrants[5].Dispose();
        Assert.Equal(12, race.ThreadsPerEncode);
    }

    [Fact]
    public void Race_EntrantTokensFollowTheJob()
    {
        using var job = new CancellationTokenSource();
        var race = new CandidateRace(true, long.MaxValue, 2);
        using var e = race.Join(Lossy, 1, job.Token);

        job.Cancel();

        Assert.True(e.Token.IsCancellationRequested);
        Assert.Null(e.AbandonReason);
    }

    private sealed class StubAdapter(OutputFormat format) : IEncoderAdapter
    {
        public string Id => "stub." + format.ToString().ToLowerInvariant();

        public OutputFormat Format => format;

        public IReadOnlyList<ToolId> RequiredTools => [];

        public ParameterScale Scale => ParameterScale.None;

        public bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source) => true;

        public double InitialGuess(double targetScore, ImageAnalysis a) => 0;

        public IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath) => [];

        public Task<EncodeOutcome> EncodeAsync(EncodeRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
