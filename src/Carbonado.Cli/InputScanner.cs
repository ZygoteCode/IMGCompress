using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Cli;

/// <summary>
/// Turns command-line paths into images to compress: files as given (content decides whether they are images),
/// folders expanded to the files with an image extension (subfolders with <c>-r</c>), and simple wildcards in the
/// file name (<c>photos\*.jpg</c>; Windows shells do not expand them). Carbonado's own output and backup folders are
/// never descended into, so a second run does not compress its own outputs; links to folders are not followed.
/// </summary>
internal static class InputScanner
{
    /// <summary>An image to compress and the folder argument it was found under (null for files given directly).</summary>
    internal sealed record Input(string Path, string? Root);

    /// <summary>The images in argument order (sorted within folders, without duplicates) and the arguments that matched nothing.</summary>
    public static (IReadOnlyList<Input> Inputs, IReadOnlyList<string> Missing) Expand(IReadOnlyList<string> paths, bool recursive, OutputPlan plan)
    {
        var inputs = new List<Input>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skip = SkippedFolderNames(plan);
        string? outputDir = plan.Location == OutputLocation.CustomFolder && plan.CustomFolder is { } custom ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(custom)) : null;

        void Add(string file, string? root)
        {
            if (seen.Add(file))
            {
                inputs.Add(new Input(file, root));
            }
        }

        foreach (string raw in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                missing.Add(raw);
                continue;
            }

            string name = Path.GetFileName(full);
            if (name.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                string dir = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;
                var matches = Directory.Exists(dir) ? Files(dir, name, recursive, skip, outputDir, imagesOnly: false).ToList() : [];
                if (matches.Count == 0)
                {
                    missing.Add(raw);
                }

                matches.ForEach(f => Add(f, recursive ? dir : null));
            }
            else if (File.Exists(full))
            {
                Add(full, null);
            }
            else if (Directory.Exists(full))
            {
                foreach (string file in Files(full, "*", recursive, skip, outputDir, imagesOnly: true))
                {
                    Add(file, full);
                }
            }
            else
            {
                missing.Add(raw);
            }
        }

        return (inputs, missing);
    }

    private static HashSet<string> SkippedFolderNames(OutputPlan plan)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OutputPathResolver.BackupFolderName };
        if (plan.Location == OutputLocation.SubfolderNextToSource && !plan.ReplaceOriginals)
        {
            names.Add(OutputPathResolver.SanitizeFileName(plan.SubfolderName, "Carbonado"));
        }

        return names;
    }

    private static IEnumerable<string> Files(string root, string pattern, bool recursive, HashSet<string> skip, string? outputDir, bool imagesOnly)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, MatchCasing = MatchCasing.CaseInsensitive };
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(dir, pattern, options).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!imagesOnly || FormatInfo.IsInputExtension(file))
                {
                    yield return file;
                }
            }

            if (!recursive)
            {
                continue;
            }

            var subfolders = new DirectoryInfo(dir).EnumerateDirectories("*", options)
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0 && !skip.Contains(d.Name)
                    && !string.Equals(d.FullName, outputDir, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.FullName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Reverse();
            foreach (string sub in subfolders)
            {
                pending.Push(sub);
            }
        }
    }
}
