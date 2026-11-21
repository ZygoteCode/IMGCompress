using System.Text.RegularExpressions;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;
using Carbonado.Core.Persistence;

namespace Carbonado.Core.Migration;

/// <summary>
/// Finds traces of IMGCompress 6 and migrates its workflow. The legacy app stored no settings: its "data" was the
/// <c>inputs</c>/<c>outputs</c> folders next to the executable, the bundled ffmpeg/pngquant binaries, and temp files
/// it leaked in <c>&lt;SystemDrive&gt;:\Temp</c> (28 random alphanumerics, optionally "-or8", .png/.jpg/.webp/.avif).
/// </summary>
public sealed partial class LegacyMigrator
{
    private const int MaxDirectoriesVisited = 4000;

    /// <summary>Default legacy temp folder (e.g. C:\Temp).</summary>
    public static string DefaultLegacyTempDir =>
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Temp");

    /// <summary>Exact pattern of legacy temp files.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9]{28}(-or8)?\.(png|jpg|webp|avif)$", RegexOptions.CultureInvariant)]
    public static partial Regex OrphanPattern();

    /// <summary>
    /// Scans <paramref name="roots"/> (and sub-folders up to <paramref name="maxDepth"/>) for legacy installs, and
    /// <paramref name="legacyTempDir"/> (default <see cref="DefaultLegacyTempDir"/>) for orphan temp files.
    /// </summary>
    public LegacyScanResult Scan(IEnumerable<string> roots, string? legacyTempDir = null, int maxDepth = 3)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var installs = new List<LegacyInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;

        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
            queue.Enqueue((new DirectoryInfo(root), 0));
            while (queue.Count > 0 && visited < MaxDirectoriesVisited)
            {
                var (dir, depth) = queue.Dequeue();
                visited++;
                if (!seen.Add(dir.FullName))
                {
                    continue;
                }

                if (Probe(dir) is { } install)
                {
                    installs.Add(install);
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var child in SafeSubdirectories(dir))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return new LegacyScanResult(installs, FindOrphans(legacyTempDir ?? DefaultLegacyTempDir));
    }

    /// <summary>
    /// Applies the chosen migration steps to <paramref name="settings"/>; image paths to import are handed to
    /// <paramref name="enqueue"/>. Orphans are only ever moved to the Recycle Bin (recoverable).
    /// </summary>
    public MigrationReport Apply(
        LegacyScanResult scan,
        MigrationOptions options,
        AppSettings settings,
        Action<IReadOnlyList<string>> enqueue,
        IRecycleBin recycleBin)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(enqueue);
        ArgumentNullException.ThrowIfNull(recycleBin);

        var notes = new List<string>();
        var primary = scan.Installs.FirstOrDefault(i => i.InputsDir is not null || i.OutputsDir is not null)
                      ?? (scan.Installs.Count > 0 ? scan.Installs[0] : null);
        string profileId = LegacyLevels.ProfileId(options.LegacyLevel);
        settings.ActiveProfileId = profileId;

        int imported = 0;
        if (options.ImportInputs)
        {
            var files = scan.Installs
                .Where(i => i.InputsDir is not null)
                .SelectMany(i => ImageFiles(i.InputsDir!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count > 0)
            {
                enqueue(files);
            }
            else
            {
                notes.Add("inputs-empty");
            }

            imported = files.Count;
        }

        string? destination = null;
        if (options.UseOutputsAsDestination && primary?.OutputsDir is { } outputs)
        {
            destination = outputs;
            settings.Output = settings.Output with { Location = OutputLocation.CustomFolder, CustomFolder = outputs, MirrorTree = false };
        }

        int toolsRegistered = 0;
        if (options.RegisterLegacyTools)
        {
            foreach (var install in scan.Installs.Where(i => i.FfmpegPath is not null || i.PngquantPath is not null))
            {
                if (!settings.ExtraToolDirs.Contains(install.Folder, StringComparer.OrdinalIgnoreCase))
                {
                    settings.ExtraToolDirs.Add(install.Folder);
                    toolsRegistered++;
                }
            }
        }

        bool watchCreated = false;
        if (options.CreateWatchRule && primary?.InputsDir is { } inputs)
        {
            if (!settings.WatchRules.Any(r => string.Equals(r.Folder, inputs, StringComparison.OrdinalIgnoreCase)))
            {
                var output = primary.OutputsDir is { } o
                    ? new OutputPlan(OutputLocation.CustomFolder, o)
                    : settings.Output;
                settings.WatchRules.Add(new WatchRule("legacy-inputs", inputs, Recursive: false, profileId, output, Enabled: true));
                watchCreated = true;
            }
            else
            {
                notes.Add("watch-rule-exists");
            }
        }

        int recycled = 0;
        long freed = 0;
        if (options.CleanOrphans)
        {
            foreach (var orphan in scan.Orphans)
            {
                if (recycleBin.Send(orphan.Path))
                {
                    recycled++;
                    freed += orphan.Bytes;
                }
            }

            if (recycled < scan.Orphans.Count)
            {
                notes.Add("some-orphans-not-recycled");
            }
        }

        settings.LegacyScanDone = true;
        Log.Info($"Legacy migration: imported={imported} dest={destination ?? "-"} profile={profileId} tools={toolsRegistered} watch={watchCreated} orphans={recycled}");
        return new MigrationReport(imported, destination, profileId, toolsRegistered, watchCreated, recycled, freed, notes);
    }

    /// <summary>Image files directly inside <paramref name="folder"/> (the legacy app was not recursive).</summary>
    public static IReadOnlyList<string> ImageFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(FormatInfo.IsInputExtension)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static LegacyInstall? Probe(DirectoryInfo dir)
    {
        string Combine(string name) => Path.Combine(dir.FullName, name);
        string? exe = File.Exists(Combine("IMGCompress.exe")) ? Combine("IMGCompress.exe") : null;
        string? inputs = Directory.Exists(Combine("inputs")) ? Combine("inputs") : null;
        string? outputs = Directory.Exists(Combine("outputs")) ? Combine("outputs") : null;
        string? ffmpeg = File.Exists(Combine("ffmpeg.exe")) ? Combine("ffmpeg.exe") : null;
        string? pngquant = File.Exists(Combine("pngquant.exe")) ? Combine("pngquant.exe") : null;

        bool isLegacy = exe is not null || (inputs is not null && outputs is not null && (ffmpeg is not null || pngquant is not null));
        if (!isLegacy)
        {
            return null;
        }

        return new LegacyInstall(
            dir.FullName,
            exe,
            inputs,
            inputs is null ? 0 : ImageFiles(inputs).Count,
            outputs,
            outputs is null ? 0 : ImageFiles(outputs).Count,
            ffmpeg,
            pngquant);
    }

    private static IEnumerable<DirectoryInfo> SafeSubdirectories(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0
                            && !d.Name.StartsWith('.')
                            && !d.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static List<LegacyOrphan> FindOrphans(string tempDir)
    {
        var orphans = new List<LegacyOrphan>();
        if (!Directory.Exists(tempDir))
        {
            return orphans;
        }

        try
        {
            foreach (var file in new DirectoryInfo(tempDir).EnumerateFiles())
            {
                if (OrphanPattern().IsMatch(file.Name))
                {
                    orphans.Add(new LegacyOrphan(file.FullName, file.Length, file.LastWriteTimeUtc));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not scan legacy temp {tempDir}: {e.Message}");
        }

        return orphans;
    }
}
