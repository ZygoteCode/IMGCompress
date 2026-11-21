namespace Carbonado.Core.Engine;

/// <summary>Outcome of a <see cref="QualitySearch"/>.</summary>
/// <param name="Best">
/// The chosen passing probe: the smallest one reaching the score target, or the best-scoring one within the byte
/// budget. Null when no probe passed; <paramref name="Probes"/> then still holds every attempt (best effort).
/// </param>
/// <param name="Probes">Every probe, in evaluation order.</param>
public sealed record SearchResult(Probe? Best, IReadOnlyList<Probe> Probes);
