namespace Carbonado.Core.Engine;

public enum OutputLocation
{
    /// <summary>"&lt;source dir&gt;\&lt;SubfolderName&gt;\" (default).</summary>
    SubfolderNextToSource,

    /// <summary>Next to the source file.</summary>
    SameFolder,

    /// <summary>One chosen folder (optionally mirroring the input tree).</summary>
    CustomFolder,
}

public enum ConflictPolicy
{
    /// <summary>"name (1).ext", "name (2).ext", …</summary>
    Rename,
    Overwrite,
    Skip,
}

/// <summary>Where and how outputs are written.</summary>
/// <param name="NamePattern">Tokens: {name} {ext} {format} {profile} {score} {date}. Extension is appended automatically.</param>
public sealed record OutputPlan(
    OutputLocation Location = OutputLocation.SubfolderNextToSource,
    string? CustomFolder = null,
    string SubfolderName = "Carbonado",
    string NamePattern = "{name}",
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    bool MirrorTree = false,
    bool PreserveTimestamps = true,
    bool ReplaceOriginals = false);
