using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Engine;

/// <summary>
/// Places a job's result: always through a temp file in the destination folder and <see cref="AtomicFile.Commit"/>,
/// so no destination ever holds a partial file and the original is never written (except by
/// <see cref="OutputPlan.ReplaceOriginals"/>, which first moves it to the backup folder and restores it on failure).
/// </summary>
internal static class OutputWriter
{
    /// <summary>The original file as it was when the job started.</summary>
    internal sealed record OriginalFile(string Path, long Bytes, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc)
    {
        public static OriginalFile Of(FileInfo file) => new(file.FullName, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc);
    }

    /// <summary>Moves the verified output <paramref name="produced"/> to its destination; null when the conflict policy skipped it.</summary>
    public static string? WriteOutput(string produced, CompressionJob job, OriginalFile original, OutputFormat format, double? score)
    {
        OutputPlan plan = job.Output;
        if (plan.ReplaceOriginals)
        {
            return ReplaceOriginal(produced, job, original, format);
        }

        string final = OutputPathResolver.Resolve(original.Path, job.InputRoot, plan, format, job.Profile.Id, score);

        // The original is never overwritten without ReplaceOriginals (e.g. a PNG optimized into its own folder).
        ConflictPolicy policy = plan.Conflict == ConflictPolicy.Overwrite && SamePath(final, original.Path) ? ConflictPolicy.Rename : plan.Conflict;
        return Place(produced, move: true, final, policy, plan.PreserveTimestamps ? original : null);
    }

    /// <summary>
    /// When the plan writes outputs to another folder, copies the kept original there under its own name so that folder
    /// is complete. Returns the copy, or null when nothing was written (same folder, replace mode, or skipped).
    /// </summary>
    public static string? CopyOriginal(CompressionJob job, OriginalFile original)
    {
        OutputPlan plan = job.Output;
        if (plan.ReplaceOriginals || plan.Location == OutputLocation.SameFolder)
        {
            return null;
        }

        string folder = OutputPathResolver.ResolveFolder(original.Path, job.InputRoot, plan);
        string final = Path.Combine(folder, Path.GetFileName(original.Path));
        if (SamePath(final, original.Path))
        {
            return null;
        }

        return Place(original.Path, move: false, final, plan.Conflict, plan.PreserveTimestamps ? original : null);
    }

    // Moves (or copies) a file to a temp name in the destination folder, then commits it there.
    private static string? Place(string from, bool move, string final, ConflictPolicy policy, OriginalFile? times)
    {
        string dir = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(dir);
        string temp = TempIn(dir);
        try
        {
            if (move)
            {
                File.Move(from, temp);
            }
            else
            {
                File.Copy(from, temp);
            }

            if (times is not null)
            {
                ApplyTimes(temp, times);
            }

            return AtomicFile.Commit(temp, final, policy);
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    // The original goes to "<dir>\.carbonado-backup\<name>" (a free name), the output takes "<dir>\<stem><ext>".
    private static string? ReplaceOriginal(string produced, CompressionJob job, OriginalFile original, OutputFormat format)
    {
        string dir = Path.GetDirectoryName(original.Path)!;
        string final = OutputPathResolver.Resolve(original.Path, job.InputRoot, job.Output, format, job.Profile.Id, null);
        string temp = TempIn(dir);
        string? backup = null;
        try
        {
            File.Move(produced, temp);
            if (job.Output.PreserveTimestamps)
            {
                ApplyTimes(temp, original);
            }

            string backupDir = Path.Combine(dir, OutputPathResolver.BackupFolderName);
            Directory.CreateDirectory(backupDir);
            backup = AtomicFile.FirstFreeName(Path.Combine(backupDir, Path.GetFileName(original.Path)));
            File.Move(original.Path, backup);

            string? committed = AtomicFile.Commit(temp, final, job.Output.Conflict);
            if (committed is null)
            {
                Restore(backup, original.Path);
            }

            backup = null;
            return committed;
        }
        catch
        {
            if (backup is not null && File.Exists(backup) && !File.Exists(original.Path))
            {
                Restore(backup, original.Path);
            }

            throw;
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    private static void Restore(string backup, string originalPath)
    {
        try
        {
            File.Move(backup, originalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is safe in the backup folder; say where.
            Log.Error($"Could not restore {originalPath} from its backup {backup}.", ex);
            throw new IOException($"The original could not be put back; it is safe in {backup}.", ex);
        }
    }

    private static string TempIn(string dir) => Path.Combine(dir, $".carbonado-{Guid.NewGuid():N}.tmp");

    private static void ApplyTimes(string path, OriginalFile original)
    {
        File.SetCreationTimeUtc(path, original.CreationTimeUtc);
        File.SetLastWriteTimeUtc(path, original.LastWriteTimeUtc);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
