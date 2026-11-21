namespace Carbonado.Core.Tools;

/// <summary>Where <see cref="ToolLocator"/> looks for encoder binaries.</summary>
/// <param name="SearchRoots">Tools roots in priority order, each laid out as <c>&lt;root&gt;/&lt;package&gt;/&lt;exe&gt;</c>.</param>
/// <param name="ManifestPath">The manifest with pinned hashes; null uses the first <c>manifest.json</c> found in a search root.</param>
/// <param name="VerifyHashes">Compute SHA-256 (lazily, cached) and compare with the manifest.</param>
/// <param name="LegacyDirs">Flat folders (e.g. next to an old IMGCompress.exe) searched after the roots.</param>
/// <param name="SearchPath">Finally search the directories on the PATH environment variable.</param>
public sealed record ToolLocatorOptions(
    IReadOnlyList<string> SearchRoots,
    string? ManifestPath = null,
    bool VerifyHashes = true,
    IReadOnlyList<string>? LegacyDirs = null,
    bool SearchPath = true);
