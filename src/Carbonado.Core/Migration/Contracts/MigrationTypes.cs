namespace Carbonado.Core.Migration;

/// <summary>A folder recognised as a legacy IMGCompress installation or workspace.</summary>
public sealed record LegacyInstall(
    string Folder,
    string? ExePath,
    string? InputsDir,
    int InputImageCount,
    string? OutputsDir,
    int OutputImageCount,
    string? FfmpegPath,
    string? PngquantPath);

/// <summary>A temp file leaked by the legacy pipeline in &lt;SystemDrive&gt;:\Temp.</summary>
public sealed record LegacyOrphan(string Path, long Bytes, DateTime LastWriteUtc);

public sealed record LegacyScanResult(IReadOnlyList<LegacyInstall> Installs, IReadOnlyList<LegacyOrphan> Orphans)
{
    public long OrphanBytes => Orphans.Sum(o => o.Bytes);

    public bool FoundAnything => Installs.Count > 0 || Orphans.Count > 0;

    public static LegacyScanResult Empty { get; } = new([], []);
}

/// <param name="LegacyLevel">1 = JPG, 2 = WEBP, 3 = AVIF, 4 = JXL (legacy default).</param>
public sealed record MigrationOptions(
    bool ImportInputs,
    bool UseOutputsAsDestination,
    int LegacyLevel,
    bool RegisterLegacyTools,
    bool CreateWatchRule,
    bool CleanOrphans);

public sealed record MigrationReport(
    int ImportedFiles,
    string? Destination,
    string ProfileId,
    int ToolsRegistered,
    bool WatchRuleCreated,
    int OrphansRecycled,
    long BytesFreed,
    IReadOnlyList<string> Notes);
