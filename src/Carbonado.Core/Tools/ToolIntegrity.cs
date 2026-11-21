namespace Carbonado.Core.Tools;

public enum ToolIntegrity
{
    /// <summary>SHA-256 matches the pinned value in tools/manifest.json.</summary>
    Verified,

    /// <summary>No pinned hash for this file (e.g. found on PATH or in a legacy folder).</summary>
    Unknown,

    /// <summary>SHA-256 differs from the pinned value: the binary was modified or replaced.</summary>
    Mismatch,
}
