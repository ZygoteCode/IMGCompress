using System.Buffers;
using System.Globalization;
using System.Text;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>Where an output goes, from the input path and the <see cref="OutputPlan"/>.</summary>
public static class OutputPathResolver
{
    /// <summary>The name of the folder next to each original where <see cref="OutputPlan.ReplaceOriginals"/> keeps backups.</summary>
    public const string BackupFolderName = ".carbonado-backup";

    private static readonly SearchValues<char> s_invalid = SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The full output path for <paramref name="inputPath"/> encoded as <paramref name="format"/>.
    /// </summary>
    /// <remarks>
    /// <para>Folder: <see cref="OutputLocation.SubfolderNextToSource"/> → "&lt;source dir&gt;\&lt;SubfolderName&gt;";
    /// <see cref="OutputLocation.SameFolder"/> → the source folder; <see cref="OutputLocation.CustomFolder"/> → the
    /// chosen folder, plus the input's folder relative to <paramref name="inputRoot"/> when
    /// <see cref="OutputPlan.MirrorTree"/> is set and the input lies under it.</para>
    /// <para>Name: <see cref="OutputPlan.NamePattern"/> with the tokens {name} (input name without extension), {ext}
    /// (input extension without dot, lower case), {format} (output format, lower case: jxl, avif, webp, jpeg, png, gif),
    /// {profile} (profile id), {score} (one decimal, invariant culture; "lossless" when null) and {date} (yyyy-MM-dd,
    /// local time); then the output format's extension. Characters invalid in file names become '_', trailing dots
    /// and spaces are trimmed, reserved device names get a '_' prefix, and an empty result falls back to {name}.</para>
    /// <para><see cref="OutputPlan.ReplaceOriginals"/> ignores location and pattern: the output goes next to the
    /// original as "&lt;name&gt;&lt;new extension&gt;".</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The path is empty, or a custom folder is required but missing.</exception>
    public static string Resolve(string inputPath, string? inputRoot, OutputPlan plan, OutputFormat format, string profileId, double? score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(plan);
        string input = Path.GetFullPath(inputPath);
        string stem = Path.GetFileNameWithoutExtension(input);
        string extension = FormatInfo.Extension(format);
        if (plan.ReplaceOriginals)
        {
            return Path.Combine(Path.GetDirectoryName(input)!, SanitizeFileName(stem, "image") + extension);
        }

        string name = ExpandPattern(plan.NamePattern, input, format, profileId, score);
        return Path.Combine(ResolveFolder(input, inputRoot, plan), name + extension);
    }

    /// <summary>The folder an output of <paramref name="inputPath"/> goes to (see <see cref="Resolve"/>).</summary>
    public static string ResolveFolder(string inputPath, string? inputRoot, OutputPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(plan);
        string input = Path.GetFullPath(inputPath);
        string sourceDir = Path.GetDirectoryName(input)!;
        if (plan.ReplaceOriginals)
        {
            return sourceDir;
        }

        switch (plan.Location)
        {
            case OutputLocation.SameFolder:
                return sourceDir;

            case OutputLocation.CustomFolder:
                if (string.IsNullOrWhiteSpace(plan.CustomFolder))
                {
                    throw new ArgumentException("The output plan has no custom folder.", nameof(plan));
                }

                string custom = Path.GetFullPath(plan.CustomFolder);
                return plan.MirrorTree && RelativeFolder(sourceDir, inputRoot) is { Length: > 0 } relative
                    ? Path.Combine(custom, relative)
                    : custom;

            default:
                string sub = SanitizeFileName(plan.SubfolderName, "Carbonado");
                return Path.Combine(sourceDir, sub);
        }
    }

    /// <summary>
    /// <paramref name="name"/> made safe as a file or folder name: invalid characters become '_', trailing dots and
    /// spaces are trimmed, a reserved device name gets a '_' prefix, and an empty result becomes <paramref name="fallback"/>.
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(s_invalid.Contains(c) ? '_' : c);
        }

        string clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '.'))
        {
            return fallback;
        }

        string device = clean.Split('.')[0].TrimEnd();
        return s_reserved.Contains(device) ? "_" + clean : clean;
    }

    // Single pass, so token-like text inside a value (a file literally named "{date}.png") is never expanded again.
    private static string ExpandPattern(string? pattern, string input, OutputFormat format, string? profileId, double? score)
    {
        string stem = Path.GetFileNameWithoutExtension(input);
        string text = string.IsNullOrWhiteSpace(pattern) ? "{name}" : pattern;
        var sb = new StringBuilder(text.Length + stem.Length);
        int i = 0;
        while (i < text.Length)
        {
            int close = text.IndexOf('}', i);
            int open = close < 0 ? -1 : text.LastIndexOf('{', close, close - i + 1);
            if (close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            if (open < 0)
            {
                sb.Append(text, i, close + 1 - i);
                i = close + 1;
                continue;
            }

            sb.Append(text, i, open - i);
            string token = text[(open + 1)..close];
            string? value = token.ToLowerInvariant() switch
            {
                "name" => stem,
                "ext" => Path.GetExtension(input).TrimStart('.').ToLowerInvariant(),
                "format" => format.ToString().ToLowerInvariant(),
                "profile" => profileId ?? string.Empty,
                "score" => score is { } s && double.IsFinite(s) ? s.ToString("0.0", CultureInfo.InvariantCulture) : "lossless",
                "date" => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => null,
            };
            sb.Append(value ?? text[open..(close + 1)]);
            i = close + 1;
        }

        return SanitizeFileName(sb.ToString(), SanitizeFileName(stem, "image"));
    }

    // The source folder relative to the input root, or null when it does not lie strictly under the root.
    private static string? RelativeFolder(string sourceDir, string? inputRoot)
    {
        if (string.IsNullOrWhiteSpace(inputRoot))
        {
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        string relative = Path.GetRelativePath(root, sourceDir);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return string.Join(Path.DirectorySeparatorChar, relative.Split(Path.DirectorySeparatorChar).Select(part => SanitizeFileName(part, "_")));
    }
}
