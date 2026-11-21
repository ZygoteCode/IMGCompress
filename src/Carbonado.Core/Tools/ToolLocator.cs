using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Carbonado.Core.Tools;

/// <summary>
/// Finds Carbonado's external tools — search roots (<c>&lt;root&gt;/&lt;package&gt;/&lt;exe&gt;</c>), then legacy folders, then
/// PATH — and reports their integrity against the SHA-256 pinned in <c>tools/manifest.json</c>. Lookups are lazy and
/// thread-safe; each binary is hashed at most once per process for a given (path, size, modification time).
/// </summary>
public sealed class ToolLocator
{
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), Lazy<string>> s_hashes = new();

    private readonly ToolLocatorOptions _options;
    private readonly Lazy<ToolManifest?> _manifest;
    private readonly Lazy<Location?>[] _locations;
    private readonly Lazy<ToolInfo?>[] _infos;

    public ToolLocator(ToolLocatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SearchRoots);
        _options = options;
        _manifest = new Lazy<ToolManifest?>(LoadManifest);
        var ids = Enum.GetValues<ToolId>();
        _locations = new Lazy<Location?>[ids.Length];
        _infos = new Lazy<ToolInfo?>[ids.Length];
        foreach (ToolId id in ids)
        {
            _locations[(int)id] = new Lazy<Location?>(() => Locate(id));
            _infos[(int)id] = new Lazy<ToolInfo?>(() => Describe(id));
        }
    }

    /// <summary>The manifest used for integrity checks, or null when none was found or it is unreadable.</summary>
    public ToolManifest? Manifest => _manifest.Value;

    /// <summary>Every tool that was found, in <see cref="ToolId"/> order (hashes all of them when verifying).</summary>
    public IReadOnlyList<ToolInfo> Discover() => Enum.GetValues<ToolId>().Select(Get).OfType<ToolInfo>().ToList();

    /// <summary>The tool's location and integrity, or null when it is not installed.</summary>
    public ToolInfo? Get(ToolId id) => _infos[(int)id].Value;

    /// <summary>True when the tool exists (no hashing).</summary>
    public bool Has(ToolId id) => _locations[(int)id].Value is not null;

    /// <summary>
    /// Standard options for an app installed in <paramref name="appBaseDir"/>: <c>&lt;app&gt;/tools</c>, then the nearest
    /// ancestor <c>tools</c> folder holding a manifest (a development checkout), the app folder itself as a legacy
    /// location (ffmpeg/pngquant next to an old IMGCompress.exe), then PATH.
    /// </summary>
    public static ToolLocatorOptions DefaultOptions(string appBaseDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(appBaseDir);
        string baseDir = Path.GetFullPath(appBaseDir);
        var roots = new List<string> { Path.Combine(baseDir, "tools") };
        for (var dir = new DirectoryInfo(baseDir).Parent; dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                if (!roots.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(candidate);
                }

                break;
            }
        }

        string? manifest = roots.Select(r => Path.Combine(r, "manifest.json")).FirstOrDefault(File.Exists);
        return new ToolLocatorOptions(roots, manifest, VerifyHashes: true, LegacyDirs: [baseDir]);
    }

    /// <summary>Package folder and executable name of a tool in the standard layout.</summary>
    public static (string Package, string FileName) Layout(ToolId id) => id switch
    {
        ToolId.Cjxl => ("libjxl", "cjxl.exe"),
        ToolId.Djxl => ("libjxl", "djxl.exe"),
        ToolId.Jxlinfo => ("libjxl", "jxlinfo.exe"),
        ToolId.Ssimulacra2 => ("libjxl", "ssimulacra2.exe"),
        ToolId.Avifenc => ("libavif", "avifenc.exe"),
        ToolId.Avifdec => ("libavif", "avifdec.exe"),
        ToolId.Cwebp => ("libwebp", "cwebp.exe"),
        ToolId.Dwebp => ("libwebp", "dwebp.exe"),
        ToolId.Gif2webp => ("libwebp", "gif2webp.exe"),
        ToolId.Oxipng => ("oxipng", "oxipng.exe"),
        ToolId.Ect => ("ect", "ect.exe"),
        ToolId.Pngquant => ("pngquant", "pngquant.exe"),
        ToolId.Ffmpeg => ("ffmpeg", "ffmpeg.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    private enum Origin
    {
        Root,
        Legacy,
        SystemPath,
    }

    private sealed record Location(string Path, Origin Origin, string RelativePath);

    private ToolManifest? LoadManifest()
    {
        string? path = _options.ManifestPath
            ?? _options.SearchRoots.Select(r => SafeCombine(r, null, "manifest.json")).FirstOrDefault(p => p is not null && File.Exists(p));
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return ToolManifest.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Without a readable manifest nothing can be verified: every tool reports Unknown integrity.
            return null;
        }
    }

    private Location? Locate(ToolId id)
    {
        var (package, exe) = Layout(id);
        string relative = $"{package}/{exe}";
        foreach (string root in _options.SearchRoots)
        {
            if (Exists(root, package, exe) is { } path)
            {
                return new Location(path, Origin.Root, relative);
            }
        }

        foreach (string dir in _options.LegacyDirs ?? [])
        {
            if (Exists(dir, null, exe) is { } path)
            {
                return new Location(path, Origin.Legacy, relative);
            }
        }

        if (_options.SearchPath)
        {
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string dir in dirs)
            {
                if (Exists(dir.Trim('"'), null, exe) is { } path)
                {
                    return new Location(path, Origin.SystemPath, relative);
                }
            }
        }

        return null;
    }

    private ToolInfo? Describe(ToolId id)
    {
        if (_locations[(int)id].Value is not { } location)
        {
            return null;
        }

        string package = location.Origin switch
        {
            Origin.Root => Layout(id).Package,
            Origin.Legacy => "legacy",
            _ => "path",
        };
        ToolManifestPackage? pinnedPackage = null;
        string pinned = string.Empty;
        if (Manifest?.TryFind(location.RelativePath, out var p, out pinned) == true)
        {
            pinnedPackage = p;
        }

        if (!_options.VerifyHashes)
        {
            string? expectedVersion = location.Origin == Origin.Root ? pinnedPackage?.Version : null;
            return new ToolInfo(id, location.Path, pinnedPackage?.Name ?? package, expectedVersion, ToolIntegrity.Unknown, string.Empty);
        }

        string? sha = Hash(location.Path);
        if (sha is null || pinnedPackage is null)
        {
            return new ToolInfo(id, location.Path, package, null, ToolIntegrity.Unknown, sha ?? string.Empty);
        }

        bool matches = string.Equals(sha, pinned, StringComparison.OrdinalIgnoreCase);
        if (location.Origin == Origin.Root)
        {
            return new ToolInfo(id, location.Path, pinnedPackage.Name, matches ? pinnedPackage.Version : null, matches ? ToolIntegrity.Verified : ToolIntegrity.Mismatch, sha);
        }

        // Legacy/PATH copies are not pinned locations: identical bytes are verified, anything else is merely unknown.
        return new ToolInfo(id, location.Path, package, matches ? pinnedPackage.Version : null, matches ? ToolIntegrity.Verified : ToolIntegrity.Unknown, sha);
    }

    private static string? Exists(string dir, string? package, string exe)
    {
        string? path = SafeCombine(dir, package, exe);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static string? SafeCombine(string dir, string? package, string file)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(package is null ? Path.Combine(dir, file) : Path.Combine(dir, package, file));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    // SHA-256 (lower-case hex), computed once per (path, size, mtime); null when the file cannot be read.
    private static string? Hash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
            var lazy = s_hashes.GetOrAdd(key, static k => new Lazy<string>(() =>
            {
                using var stream = new FileStream(k.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
                return Convert.ToHexStringLower(SHA256.HashData(stream));
            }));
            try
            {
                return lazy.Value;
            }
            catch
            {
                s_hashes.TryRemove(key, out _);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
