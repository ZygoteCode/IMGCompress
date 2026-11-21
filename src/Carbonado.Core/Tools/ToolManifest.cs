using System.Text.Json;

namespace Carbonado.Core.Tools;

/// <summary>One package of <c>tools/manifest.json</c>: where its binaries come from and their pinned SHA-256 hashes.</summary>
/// <param name="Files">Pinned files: path relative to the tools root with '/' separators (e.g. "libjxl/cjxl.exe") → lower-case SHA-256 hex.</param>
public sealed record ToolManifestPackage(string Name, string Version, string Source, string? ArchiveSha256, IReadOnlyDictionary<string, string> Files);

/// <summary>The parsed <c>tools/manifest.json</c> (versions, download sources, pinned SHA-256 of every binary).</summary>
public sealed class ToolManifest
{
    private ToolManifest(IReadOnlyList<ToolManifestPackage> packages) => Packages = packages;

    public IReadOnlyList<ToolManifestPackage> Packages { get; }

    /// <summary>Reads and validates a manifest file.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid manifest.</exception>
    public static ToolManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] json = File.ReadAllBytes(path);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw Malformed(path, ex.Message);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("packages", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw Malformed(path, "missing 'packages' array");
            }

            var packages = new List<ToolManifestPackage>();
            foreach (JsonElement p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed(path, "each package needs a 'files' object");
                }

                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty f in filesElement.EnumerateObject())
                {
                    files[NormalizeRelative(f.Name)] = f.Value.ValueKind == JsonValueKind.String
                        ? f.Value.GetString()!.ToLowerInvariant()
                        : throw Malformed(path, $"hash of '{f.Name}' is not a string");
                }

                string? archive = p.TryGetProperty("archiveSha256", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                packages.Add(new ToolManifestPackage(Text(p, "name", path), Text(p, "version", path), Text(p, "source", path), archive, files));
            }

            return new ToolManifest(packages);
        }
    }

    /// <summary>Finds the package pinning <paramref name="relativePath"/> ("libjxl/cjxl.exe"), with its expected hash.</summary>
    internal bool TryFind(string relativePath, out ToolManifestPackage package, out string sha256)
    {
        string key = NormalizeRelative(relativePath);
        foreach (var p in Packages)
        {
            if (p.Files.TryGetValue(key, out string? hash))
            {
                package = p;
                sha256 = hash;
                return true;
            }
        }

        package = null!;
        sha256 = string.Empty;
        return false;
    }

    private static string Text(JsonElement e, string name, string path) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Malformed(path, $"missing string '{name}'");

    private static InvalidDataException Malformed(string path, string detail) => new($"The tools manifest '{path}' is malformed: {detail}.");

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').TrimStart('/');
}
