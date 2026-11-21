using Carbonado.Core.Encoders;

namespace Carbonado.Core.Engine;

/// <summary>
/// Searches an encoder's quality parameter with as few encodes as possible: the smallest output whose SSIMULACRA2
/// reaches a target (<see cref="ForScoreAsync"/>), or the best-scoring output within a byte budget
/// (<see cref="ForSizeAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both searches assume the goal is monotone along the "quality direction" of the <see cref="ParameterScale"/>
/// (higher values when <see cref="ParameterScale.HigherIsBetter"/>, lower values otherwise) and tolerate noise: the
/// result is always chosen among the probes actually measured.
/// </para>
/// <para>
/// Algorithm: probe the initial guess; while no probe passes, step toward the passing side by an adaptive step
/// (5 % of the range, doubled after each probe) until one passes or the bound is reached; while no probe fails, step
/// the other way to find a failing one (a passing probe far above the goal wastes bytes). Then interpolate with the
/// secant between the nearest failing and passing probes (bisecting instead when the same end moved twice in a row),
/// snapping to <see cref="ParameterScale.Resolution"/> on the passing side and strictly inside the bracket. The search
/// stops when the best passing probe is good enough (score within the tolerance above the target; size within 2 %
/// under the budget), when no grid point is left inside the bracket, after <c>maxIterations</c> probes, or when a
/// parameter would repeat.
/// </para>
/// <para>The searches are not thread-safe per call but are stateless: any number may run concurrently.</para>
/// </remarks>
public static class QualitySearch
{
    // The first bracketing step, as a fraction of the scale's range (then doubled after every step).
    private const double InitialStepFraction = 1.0 / 20;

    // Size mode: interpolate toward 99 % of the budget; a probe using at least 98 % of it is good enough.
    private const double SizeAimFraction = 0.99;
    private const double SizeGoodEnoughFraction = 0.98;

    /// <summary>
    /// Finds the parameter giving the smallest output whose score is at least <paramref name="target"/>.
    /// </summary>
    /// <param name="evaluate">Encodes at a parameter and measures the result; must honour cancellation.</param>
    /// <param name="scale">The encoder's parameter space; every probed parameter lies on its grid.</param>
    /// <param name="target">The SSIMULACRA2 score to reach.</param>
    /// <param name="initialGuess">The first parameter to try (clamped and rounded to the scale).</param>
    /// <param name="maxIterations">Most probes to evaluate (at least 1).</param>
    /// <param name="tolerance">A passing probe at most this far above <paramref name="target"/> ends the search.</param>
    /// <param name="ct">Cancels the search (the exception from <paramref name="evaluate"/> propagates).</param>
    /// <returns>The smallest passing probe (null when none passed) and every probe in evaluation order.</returns>
    public static Task<SearchResult> ForScoreAsync(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, double target, double initialGuess, int maxIterations, double tolerance, CancellationToken ct)
    {
        Validate(evaluate, scale, maxIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        if (!double.IsFinite(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target score must be a finite number.");
        }

        var goal = new Goal(
            Direction: scale.HigherIsBetter ? 1 : -1,
            Passes: p => p.Score >= target,
            Measure: p => p.Score,
            Aim: target + (tolerance / 2),
            GoodEnough: p => p.Score - target <= tolerance,
            IsBetter: (a, b) => a.Bytes < b.Bytes || (a.Bytes == b.Bytes && a.Score > b.Score));
        return SearchAsync(evaluate, scale, initialGuess, maxIterations, goal, ct);
    }

    /// <summary>
    /// Finds the parameter giving the highest score whose output is at most <paramref name="maxBytes"/> long.
    /// </summary>
    /// <param name="evaluate">Encodes at a parameter and measures the result; must honour cancellation.</param>
    /// <param name="scale">The encoder's parameter space; every probed parameter lies on its grid.</param>
    /// <param name="maxBytes">The byte budget (at least 1).</param>
    /// <param name="initialGuess">The first parameter to try (clamped and rounded to the scale).</param>
    /// <param name="maxIterations">Most probes to evaluate (at least 1).</param>
    /// <param name="ct">Cancels the search (the exception from <paramref name="evaluate"/> propagates).</param>
    /// <returns>The best-scoring probe within the budget (null when none fits) and every probe in evaluation order.</returns>
    public static Task<SearchResult> ForSizeAsync(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, long maxBytes, double initialGuess, int maxIterations, CancellationToken ct)
    {
        Validate(evaluate, scale, maxIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // The passing side is the low-quality one: walking toward it means fewer bytes.
        var goal = new Goal(
            Direction: scale.HigherIsBetter ? -1 : 1,
            Passes: p => p.Bytes <= maxBytes,
            Measure: p => -Math.Log(Math.Max(1, p.Bytes)),
            Aim: -Math.Log(maxBytes * SizeAimFraction),
            GoodEnough: p => p.Bytes >= maxBytes * SizeGoodEnoughFraction,
            IsBetter: (a, b) => a.Score > b.Score || (a.Score == b.Score && a.Bytes < b.Bytes));
        return SearchAsync(evaluate, scale, initialGuess, maxIterations, goal, ct);
    }

    private static void Validate(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, int maxIterations)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        if (!(scale.Max >= scale.Min))
        {
            throw new ArgumentException("The parameter scale is empty (Max < Min).", nameof(scale));
        }
    }

    private static async Task<SearchResult> SearchAsync(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, double initialGuess, int maxIterations, Goal goal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var run = new Run(evaluate, scale, goal, maxIterations);
        double guess = double.IsFinite(initialGuess) ? initialGuess : (scale.Min + scale.Max) / 2;
        await run.ProbeAsync(run.ToIndex(scale.Normalize(guess)), ct).ConfigureAwait(false);

        // Bracketing: walk with a doubling step until there is a failing probe below a passing one.
        long step = Math.Max(1, (long)Math.Round((run.MaxIndex - run.MinIndex) * InitialStepFraction));
        while (!run.Exhausted && (run.Low is null || run.High is null))
        {
            long next;
            if (run.High is null)
            {
                // Nothing passes yet: move toward the passing side.
                if (run.Low!.Index >= run.MaxIndex)
                {
                    break;
                }

                next = Math.Min(run.Low.Index + step, run.MaxIndex);
            }
            else
            {
                // Nothing fails yet: stop when good enough, else look for the failing side.
                if (run.IsGoodEnough || run.High.Index <= run.MinIndex)
                {
                    break;
                }

                next = Math.Max(run.High.Index - step, run.MinIndex);
            }

            step = step > long.MaxValue / 4 ? step : step * 2;
            if (!await run.ProbeAsync(next, ct).ConfigureAwait(false))
            {
                break;
            }
        }

        // Refinement: secant on (index, measure) inside the bracket, bisecting when one end is stuck.
        while (!run.Exhausted && run.Low is { } low && run.High is { } high && !run.IsGoodEnough && high.Index - low.Index > 1)
        {
            double target = run.StuckSide ? (low.Index + high.Index) / 2.0 : Secant(low, high, goal.Aim);
            long next = (long)Math.Ceiling(target - 1e-9);
            next = Math.Clamp(next, low.Index + 1, high.Index - 1);
            if (!await run.ProbeAsync(next, ct).ConfigureAwait(false))
            {
                break;
            }
        }

        return new SearchResult(run.Best, run.Probes);
    }

    // Where the line through the two bracket ends reaches the aim (the midpoint when the measures do not rise).
    private static double Secant(Point low, Point high, double aim)
    {
        double rise = high.Measure - low.Measure;
        if (!(rise > 0) || !double.IsFinite(rise))
        {
            return (low.Index + high.Index) / 2.0;
        }

        double t = Math.Clamp((aim - low.Measure) / rise, 0, 1);
        return low.Index + (t * (high.Index - low.Index));
    }

    /// <summary>What the search optimises, expressed on an axis where passing is monotone non-decreasing.</summary>
    /// <param name="Direction">+1 when larger parameters move toward the passing side, -1 otherwise.</param>
    /// <param name="Passes">The predicate a probe must satisfy.</param>
    /// <param name="Measure">A value rising toward the passing side (score, or −ln bytes), for interpolation.</param>
    /// <param name="Aim">The measure the secant aims at (just inside the passing side).</param>
    /// <param name="GoodEnough">A passing probe this close to the boundary ends the search.</param>
    /// <param name="IsBetter">Orders passing probes (true when the first beats the second).</param>
    private sealed record Goal(int Direction, Func<Probe, bool> Passes, Func<Probe, double> Measure, double Aim, Func<Probe, bool> GoodEnough, Func<Probe, Probe, bool> IsBetter);

    /// <summary>A probe placed on the search axis (grid index, oriented so passing lies toward larger indices).</summary>
    private sealed record Point(long Index, double Measure, bool Passed);

    /// <summary>The state of one search: grid mapping, probes, bracket and best passing probe.</summary>
    private sealed class Run(Func<double, CancellationToken, Task<Probe>> evaluate, ParameterScale scale, Goal goal, int maxIterations)
    {
        private readonly double _resolution = scale.Resolution > 0 ? scale.Resolution : Math.Max((scale.Max - scale.Min) / 1000, 1e-6);
        private readonly HashSet<long> _tried = [];
        private readonly List<Point> _points = [];
        private int _lastSide; // +1 high moved, -1 low moved, 0 none
        private int _sameSideMoves;

        public List<Probe> Probes { get; } = [];

        public Probe? Best { get; private set; }

        public Point? Low { get; private set; }

        public Point? High { get; private set; }

        public long MinIndex => Math.Min(ToIndex(scale.Min, ceiling: true), ToIndex(scale.Max, ceiling: true));

        public long MaxIndex => Math.Max(ToIndex(scale.Min, ceiling: false), ToIndex(scale.Max, ceiling: false));

        public bool Exhausted => Probes.Count >= maxIterations;

        public bool IsGoodEnough => Best is { } best && goal.GoodEnough(best);

        /// <summary>True after the same bracket end moved twice in a row (the secant is converging from one side).</summary>
        public bool StuckSide => _sameSideMoves >= 2;

        /// <summary>The grid index of a parameter on the search axis (rounded to the nearest grid point).</summary>
        public long ToIndex(double parameter) => (long)Math.Round(goal.Direction * parameter / _resolution, MidpointRounding.AwayFromZero);

        /// <summary>Evaluates the grid point <paramref name="index"/>; false (nothing evaluated) when it was already tried.</summary>
        public async Task<bool> ProbeAsync(long index, CancellationToken ct)
        {
            index = Math.Clamp(index, MinIndex, MaxIndex);
            if (!_tried.Add(index))
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();
            double parameter = Math.Clamp(Math.Round(goal.Direction * index * _resolution, 6), scale.Min, scale.Max);
            Probe probe = await evaluate(parameter, ct).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(probe);
            Probes.Add(probe);

            bool passed = goal.Passes(probe);
            _points.Add(new Point(index, goal.Measure(probe), passed));
            if (passed && (Best is null || goal.IsBetter(probe, Best)))
            {
                Best = probe;
            }

            UpdateBracket(passed);
            return true;
        }

        // The bracket is recomputed from every probe, which keeps it consistent even when the goal is not monotone:
        // High = the passing probe nearest the failing side, Low = the failing probe nearest below it.
        private void UpdateBracket(bool passed)
        {
            Point? previousLow = Low, previousHigh = High;
            High = _points.Where(p => p.Passed).MinBy(p => p.Index);
            Low = High is null
                ? _points.MaxBy(p => p.Index)
                : _points.Where(p => !p.Passed && p.Index < High.Index).MaxBy(p => p.Index);

            int side = passed ? (High != previousHigh ? 1 : 0) : (Low != previousLow ? -1 : 0);
            if (Low is null || High is null || side == 0)
            {
                _lastSide = 0;
                _sameSideMoves = 0;
                return;
            }

            _sameSideMoves = side == _lastSide ? _sameSideMoves + 1 : 1;
            _lastSide = side;
        }

        private long ToIndex(double parameter, bool ceiling)
        {
            double scaled = Math.Round(goal.Direction * parameter / _resolution, 9);
            return (long)(ceiling ? Math.Ceiling(scaled) : Math.Floor(scaled));
        }
    }
}
