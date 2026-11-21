namespace Carbonado.Core.Tools;

/// <summary>A located external tool.</summary>
/// <param name="Package">Manifest package name (e.g. "libjxl") or "legacy"/"path".</param>
public sealed record ToolInfo(ToolId Id, string Path, string Package, string? Version, ToolIntegrity Integrity, string Sha256);
