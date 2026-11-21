using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Persistence;

/// <summary>One line of %LOCALAPPDATA%\Carbonado\history.jsonl.</summary>
public sealed record HistoryEntry(
    DateTimeOffset At,
    string Input,
    string? Output,
    JobOutcome Outcome,
    long OriginalBytes,
    long OutputBytes,
    OutputFormat? Format,
    double? Score,
    string ProfileId,
    double Seconds);

public sealed record LifetimeStats(
    long Files,
    long OriginalBytes,
    long OutputBytes,
    IReadOnlyDictionary<OutputFormat, int> ByFormat)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - OutputBytes);

    public double SavedFraction => OriginalBytes > 0 ? (double)SavedBytes / OriginalBytes : 0;

    public static LifetimeStats Empty { get; } = new(0, 0, 0, new Dictionary<OutputFormat, int>());
}
