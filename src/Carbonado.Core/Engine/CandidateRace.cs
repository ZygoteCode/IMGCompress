using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Coordinates the concurrent candidates of one job. It shares the job's thread budget among the candidates still
/// running (each encode asks when it starts), and — when the smallest output wins — stops candidates that can no
/// longer win: every candidate has a floor (the fewest bytes it could still win with) and is cancelled, its tools
/// killed, as soon as a verified output no larger than that floor exists.
/// </summary>
/// <remarks>
/// Floors: a lossy candidate's failing probe (reaching the score needs more quality, hence more bytes, than a failing
/// probe used); a JPEG → JPEG XL transcode keeps every coefficient and never saves half of the JPEG (typically
/// 15–30 %); ECT's lossless optimization of a JPEG saves well under 40 %. Pixel-lossless candidates have no floor. With
/// <see cref="CompressionProfile.NeverLarger"/> the original's size bounds every candidate from the start.
/// </remarks>
internal sealed class CandidateRace
{
    internal const double TranscodeFloor = 0.5;
    internal const double JpegOptimizeFloor = 0.6;

    private readonly Lock _gate = new();
    private readonly List<Entrant> _entrants = [];
    private readonly bool _smallestWins;
    private readonly int _threads;
    private long _best;
    private int _active;

    /// <param name="smallestWins">The job keeps the smallest verified output (not the best score within a budget).</param>
    /// <param name="ceiling">Size an output must beat (the original's with never-larger, else unbounded).</param>
    /// <param name="threads">The job's encoder thread budget.</param>
    public CandidateRace(bool smallestWins, long ceiling, int threads)
    {
        _smallestWins = smallestWins;
        _best = ceiling;
        _threads = Math.Max(1, threads);
    }

    /// <summary>The smallest verified output so far (or the ceiling).</summary>
    public long Best => Interlocked.Read(ref _best);

    /// <summary>Encoder threads for an encode starting now: the job's budget shared by the candidates still running.</summary>
    public int ThreadsPerEncode => Math.Max(1, _threads / Math.Max(1, Volatile.Read(ref _active)));

    /// <summary>Registers a candidate; dispose the entrant when the candidate ends.</summary>
    public Entrant Join(CandidatePlan plan, long originalBytes, CancellationToken jobCt)
    {
        long floor = plan.Kind switch
        {
            EncodeKind.JpegTranscode => (long)(originalBytes * TranscodeFloor),
            EncodeKind.Optimize when plan.Format == OutputFormat.Jpeg => (long)(originalBytes * JpegOptimizeFloor),
            _ => 0,
        };
        var entrant = new Entrant(this, floor, jobCt);
        lock (_gate)
        {
            _entrants.Add(entrant);
        }

        Interlocked.Increment(ref _active);
        entrant.CheckAgainst(Best);
        return entrant;
    }

    /// <summary>A verified output of <paramref name="bytes"/> exists: candidates that cannot be smaller are stopped.</summary>
    public void Offer(long bytes)
    {
        if (!_smallestWins)
        {
            return;
        }

        long current;
        do
        {
            current = Interlocked.Read(ref _best);
            if (bytes >= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _best, bytes, current) != current);

        Entrant[] all;
        lock (_gate)
        {
            all = [.. _entrants];
        }

        foreach (Entrant entrant in all)
        {
            entrant.CheckAgainst(bytes);
        }
    }

    private void Leave(Entrant entrant)
    {
        lock (_gate)
        {
            _entrants.Remove(entrant);
        }

        Interlocked.Decrement(ref _active);
    }

    /// <summary>One candidate in the race: its cancellation (linked to the job's) and its floor.</summary>
    internal sealed class Entrant : IDisposable
    {
        private readonly CandidateRace _race;
        private readonly CancellationTokenSource _cts;
        private long _floor;
        private string? _reason;
        private int _left;

        public Entrant(CandidateRace race, long floor, CancellationToken jobCt)
        {
            _race = race;
            _floor = floor;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(jobCt);
        }

        /// <summary>Cancelled when the job is, or when this candidate can no longer win.</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>Why the candidate was stopped early, or null.</summary>
        public string? AbandonReason => Volatile.Read(ref _reason);

        /// <summary>A failing probe of <paramref name="bytes"/>: winning would need more bytes than that.</summary>
        public void RaiseFloor(long bytes)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref _floor);
                if (bytes <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _floor, bytes, current) != current);

            CheckAgainst(_race.Best);
        }

        public void CheckAgainst(long best)
        {
            if (!_race._smallestWins || Interlocked.Read(ref _floor) < best)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _reason, $"stopped early: it cannot get below {best} bytes", null) is null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The candidate ended meanwhile.
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                _race.Leave(this);
                _cts.Dispose();
            }
        }
    }
}
