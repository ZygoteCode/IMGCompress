namespace Carbonado.Core.Engine;

/// <summary>Engine-wide settings, shared by <see cref="EngineContext"/> and <see cref="BatchScheduler"/>.</summary>
/// <remarks>Out-of-range values (they may come from user settings) fall back to the defaults instead of throwing.</remarks>
/// <param name="MaxConcurrency">Most jobs running at once; null (or &lt; 1) = one per logical core, memory permitting.</param>
/// <param name="MemoryBudgetFraction">Share of physical RAM the running jobs may use together (estimated per job from its pixels); values outside (0, 1] mean 0.25.</param>
/// <param name="ScoreTolerance">A lossy candidate within this many SSIMULACRA2 points above the target ends its quality search (negative = 0).</param>
/// <param name="TempRoot">Folder under which the engine creates its private session folder; null = the user's data temp root (<see cref="Persistence.DataPaths.TempRoot"/>).</param>
public sealed record EngineOptions(int? MaxConcurrency = null, double MemoryBudgetFraction = 0.25, double ScoreTolerance = 0.8, string? TempRoot = null);
