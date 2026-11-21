using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;

namespace Carbonado.Core.Tests.Engine;

public class QualitySearchTests
{
    private static readonly ParameterScale Quality = new(0, 100, HigherIsBetter: true, Resolution: 1);
    private static readonly ParameterScale Distance = new(0.05, 15, HigherIsBetter: false, Resolution: 0.01);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A concave, saturating curve like real encoders: 40 at q 0, 85 at q ≈ 41.6, 100 at the top.
    private static double ScoreOfQuality(double q) => 100 - (60 * Math.Exp(-q / 30));

    private static long BytesOfQuality(double q) => (long)(1000 * Math.Exp(q / 20));

    [Theory]
    [InlineData(85, 40, 5)]
    [InlineData(85, 44, 5)]
    [InlineData(85, 95, 9)]
    [InlineData(85, 5, 9)]
    [InlineData(70, 60, 7)]
    [InlineData(90, 10, 9)]
    public async Task Search_FindsMinimalPassingParam(double target, double guess, int maxIterations)
    {
        var encoder = new FakeEncoder(ScoreOfQuality, BytesOfQuality);

        var result = await QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, target, guess, maxIterations, tolerance: 0.01, Ct);

        // The smallest quality on the grid whose score reaches the target.
        double optimum = Math.Ceiling(-30 * Math.Log((100 - target) / 60));
        Assert.NotNull(result.Best);
        Assert.True(result.Best.Score >= target);
        Assert.InRange(result.Best.Parameter, optimum, optimum + Quality.Resolution);
        Assert.InRange(result.Probes.Count, 1, maxIterations);
        Assert.Equal(encoder.Calls, result.Probes.Select(p => p.Parameter));
        Assert.Equal(result.Probes.Count, result.Probes.Select(p => p.Parameter).Distinct().Count());
    }

    [Fact]
    public async Task Search_StopsAsSoonAsAProbeIsWithinTolerance()
    {
        var encoder = new FakeEncoder(ScoreOfQuality, BytesOfQuality);

        // Score at q 42 ≈ 85.20: inside [85, 85.8].
        var result = await QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, 85, 42, 9, tolerance: 0.8, Ct);

        Assert.Single(result.Probes);
        Assert.Equal(42, result.Best!.Parameter);
    }

    [Theory]
    [InlineData(true, 20.0)]
    [InlineData(false, 3.0)]
    [InlineData(false, 0.5)]
    [InlineData(false, 1.7)]
    public async Task Search_HonoursDirection(bool higherIsBetter, double guess)
    {
        if (higherIsBetter)
        {
            var quality = new FakeEncoder(ScoreOfQuality, BytesOfQuality);

            var result = await QualitySearch.ForScoreAsync(quality.EvaluateAsync, Quality, 85, guess, 9, 0.01, Ct);

            // Moving toward quality means larger values; the answer is the smallest passing one.
            Assert.Equal(42, result.Best!.Parameter);
            Assert.All(result.Probes.Where(p => p.Parameter < 42), p => Assert.True(p.Score < 85));
            Assert.True(result.Probes[1].Parameter > result.Probes[0].Parameter);
        }
        else
        {
            // A butteraugli-like distance: lower is better; 85 is reached at d ≤ 1.6667.
            var distance = new FakeEncoder(d => 95 - (6 * d), d => (long)(400_000 / (d + 0.2)));

            var result = await QualitySearch.ForScoreAsync(distance.EvaluateAsync, Distance, 85, guess, 9, 0.01, Ct);

            Assert.NotNull(result.Best);
            Assert.Equal(1.66, result.Best.Parameter, 6);
            Assert.True(result.Best.Score >= 85);
            Assert.InRange(result.Probes.Count, 1, 9);
            Assert.All(result.Probes, p => Assert.Equal(p.Parameter, Distance.Normalize(p.Parameter), 9));
        }
    }

    [Fact]
    public async Task Search_UnreachableTarget_ReturnsBestEffortNullPass()
    {
        // Saturates at 80: a target of 85 can never pass.
        var encoder = new FakeEncoder(q => 40 + (0.4 * q), BytesOfQuality);

        var result = await QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, 85, 60, 7, 0.8, Ct);

        Assert.Null(result.Best);
        Assert.NotEmpty(result.Probes);
        Assert.InRange(result.Probes.Count, 1, 7);

        // Best effort: the highest-quality end of the scale was tried.
        Assert.Contains(result.Probes, p => p.Parameter == Quality.Max);
        Assert.All(result.Probes, p => Assert.True(p.Score < 85));
    }

    [Fact]
    public async Task Search_EverythingPasses_EndsAtTheLowestQuality()
    {
        var encoder = new FakeEncoder(_ => 99, BytesOfQuality);

        var result = await QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, 50, 50, 9, 0.8, Ct);

        Assert.Equal(Quality.Min, result.Best!.Parameter);
    }

    [Fact]
    public async Task Search_KeepsTheSmallestPassingProbe_EvenWhenScoresAreNotMonotone()
    {
        // Noisy encoder: q 45 scores above q 46, and q 44 is the smallest passing file.
        var scores = new Dictionary<double, double> { [40] = 83, [44] = 85.3, [45] = 86.1, [46] = 85.9, [50] = 88 };
        var encoder = new FakeEncoder(q => scores.TryGetValue(q, out double s) ? s : ScoreOfQuality(q), BytesOfQuality);

        var result = await QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, 85, 45, 9, 0.01, Ct);

        var passing = result.Probes.Where(p => p.Score >= 85).ToList();
        Assert.Equal(passing.MinBy(p => p.Bytes), result.Best);
    }

    [Fact]
    public async Task Search_Cancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int calls = 0;
        Task<Probe> Evaluate(double q, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++calls == 2)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(new Probe(q, BytesOfQuality(q), 50, "p"));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QualitySearch.ForScoreAsync(Evaluate, Quality, 85, 50, 9, 0.8, cts.Token));
        Assert.Equal(2, calls);

        // An already-cancelled token never evaluates anything.
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QualitySearch.ForSizeAsync(Evaluate, Quality, 10_000, 50, 9, cts.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(20_000, 80, 9)]
    [InlineData(20_000, 10, 9)]
    [InlineData(20_000, 58, 5)]
    [InlineData(3_000, 50, 9)]
    public async Task SizeSearch_MaximizesQualityUnderBudget(long budget, double guess, int maxIterations)
    {
        var encoder = new FakeEncoder(ScoreOfQuality, BytesOfQuality);

        var result = await QualitySearch.ForSizeAsync(encoder.EvaluateAsync, Quality, budget, guess, maxIterations, Ct);

        // The largest quality on the grid whose file fits the budget.
        double optimum = Math.Floor(20 * Math.Log(budget / 1000.0));
        Assert.NotNull(result.Best);
        Assert.True(result.Best.Bytes <= budget);
        Assert.InRange(result.Best.Parameter, optimum - Quality.Resolution, optimum);
        Assert.InRange(result.Probes.Count, 1, maxIterations);
        Assert.Equal(result.Probes.Where(p => p.Bytes <= budget).MaxBy(p => p.Score), result.Best);
    }

    [Fact]
    public async Task SizeSearch_HonoursLowerIsBetterScales()
    {
        // Distance-like scale: bytes shrink as the distance grows.
        var encoder = new FakeEncoder(d => 95 - (6 * d), d => (long)(100_000 / (d + 0.5)));

        var result = await QualitySearch.ForSizeAsync(encoder.EvaluateAsync, Distance, 40_000, 1, 9, Ct);

        // 100000 / (d + 0.5) ≤ 40000 ⇔ d ≥ 2: the best distances fit the budget; a probe within 2 % of it is good enough.
        Assert.NotNull(result.Best);
        Assert.InRange(result.Best.Bytes, (long)(40_000 * 0.98), 40_000);
        Assert.InRange(result.Best.Parameter, 2.0, 2.06);
    }

    [Fact]
    public async Task SizeSearch_UnreachableBudget_ReturnsNull()
    {
        var encoder = new FakeEncoder(ScoreOfQuality, BytesOfQuality);

        var result = await QualitySearch.ForSizeAsync(encoder.EvaluateAsync, Quality, 500, 50, 9, Ct);

        Assert.Null(result.Best);
        Assert.Contains(result.Probes, p => p.Parameter == Quality.Min);
    }

    [Fact]
    public async Task Search_RejectsInvalidArguments()
    {
        var encoder = new FakeEncoder(ScoreOfQuality, BytesOfQuality);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => QualitySearch.ForScoreAsync(encoder.EvaluateAsync, Quality, 85, 50, 0, 0.8, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => QualitySearch.ForSizeAsync(encoder.EvaluateAsync, Quality, 0, 50, 5, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => QualitySearch.ForScoreAsync(null!, Quality, 85, 50, 5, 0.8, Ct));
        Assert.Empty(encoder.Calls);
    }

    private sealed class FakeEncoder(Func<double, double> score, Func<double, long> bytes)
    {
        public List<double> Calls { get; } = [];

        public Task<Probe> EvaluateAsync(double parameter, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(parameter);
            return Task.FromResult(new Probe(parameter, bytes(parameter), score(parameter), $"probe-{parameter}"));
        }
    }
}
