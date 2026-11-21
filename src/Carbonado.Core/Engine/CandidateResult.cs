using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>The outcome of one planned candidate: a verified file in the job folder, or why there is none.</summary>
/// <param name="Plan">What was tried.</param>
/// <param name="Order">The plan's position (earlier wins ties).</param>
/// <param name="Path">The verified output, or null.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Score">Measured SSIMULACRA2 for lossy outputs; null for exact ones.</param>
/// <param name="Parameter">The winning quality parameter of a lossy search.</param>
/// <param name="Verification">How the output was proven; <see cref="VerificationKind.None"/> when there is no output.</param>
/// <param name="Error">Why the candidate produced nothing usable.</param>
/// <param name="Produced">At least one encode succeeded (false: the encoder itself failed every time).</param>
internal sealed record CandidateResult(
    CandidatePlan Plan,
    int Order,
    string? Path,
    long Bytes,
    double? Score,
    double? Parameter,
    VerificationKind Verification,
    string? Error,
    bool Produced)
{
    public bool Verified => Path is not null && Verification != VerificationKind.None;

    public OutputFormat Format => Plan.Format;

    /// <summary>The score used to rank candidates within a byte budget (exact outputs count as perfect).</summary>
    public double RankScore => Score ?? 100;

    public static CandidateResult Discarded(CandidatePlan plan, int order, string error, bool produced) =>
        new(plan, order, null, 0, null, null, VerificationKind.None, error, produced);
}
