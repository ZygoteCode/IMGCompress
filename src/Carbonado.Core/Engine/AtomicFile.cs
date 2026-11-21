using System.Globalization;

namespace Carbonado.Core.Engine;

/// <summary>
/// Puts a finished file in place with a single rename, so a destination never holds a partial file. Write the temp
/// file in the destination folder (same volume): the rename is then atomic.
/// </summary>
public static class AtomicFile
{
    // "name (1).ext" … "name (9999).ext"; beyond that the destination folder is unusable.
    private const int MaxRenameAttempts = 10_000;

    /// <summary>
    /// Moves <paramref name="tempPath"/> to <paramref name="finalPath"/> following <paramref name="policy"/>:
    /// <see cref="ConflictPolicy.Rename"/> picks the first free name among "name.ext", "name (1).ext", "name (2).ext"…;
    /// <see cref="ConflictPolicy.Overwrite"/> replaces an existing file; <see cref="ConflictPolicy.Skip"/> leaves an
    /// existing file alone. Races with other writers are handled (a name taken meanwhile is never overwritten unless
    /// the policy is Overwrite). The temp file never survives the call: it is moved, or deleted when skipped or on error.
    /// </summary>
    /// <returns>The path the file now has, or null when it was skipped.</returns>
    /// <exception cref="FileNotFoundException">The temp file does not exist.</exception>
    /// <exception cref="IOException">The destination cannot be written (a folder in the way, a locked file, a full disk…).</exception>
    /// <exception cref="UnauthorizedAccessException">The destination is read-only or not accessible.</exception>
    public static string? Commit(string tempPath, string finalPath, ConflictPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        string temp = Path.GetFullPath(tempPath);
        string final = Path.GetFullPath(finalPath);
        try
        {
            if (!File.Exists(temp))
            {
                throw new FileNotFoundException("The file to commit does not exist.", temp);
            }

            if (Path.GetDirectoryName(final) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            switch (policy)
            {
                case ConflictPolicy.Overwrite:
                    if (Directory.Exists(final))
                    {
                        throw new IOException($"A folder with the output's name is in the way: {final}");
                    }

                    File.Move(temp, final, overwrite: true);
                    return final;

                case ConflictPolicy.Skip:
                    if (!Exists(final) && TryMoveToNewName(temp, final))
                    {
                        return final;
                    }

                    TryDelete(temp);
                    return null;

                default:
                    for (int n = 0; n < MaxRenameAttempts; n++)
                    {
                        string candidate = n == 0 ? final : Numbered(final, n);
                        if (!Exists(candidate) && TryMoveToNewName(temp, candidate))
                        {
                            return candidate;
                        }
                    }

                    throw new IOException($"No free file name is left for {final}.");
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>"dir\name (n).ext" for "dir\name.ext".</summary>
    internal static string Numbered(string path, int n)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        return Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}){ext}"));
    }

    /// <summary>The first of "name.ext", "name (1).ext", … that does not exist yet (no file is created).</summary>
    internal static string FirstFreeName(string path)
    {
        for (int n = 0; n < MaxRenameAttempts; n++)
        {
            string candidate = n == 0 ? path : Numbered(path, n);
            if (!Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free file name is left for {path}.");
    }

    internal static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // False when another writer took the name between the check and the move.
    private static bool TryMoveToNewName(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: false);
            return true;
        }
        catch (IOException) when (Exists(target) && File.Exists(temp))
        {
            return false;
        }
    }
}
