namespace Carbonado.Core.Migration;

/// <summary>Moves files to the Windows Recycle Bin (recoverable deletion).</summary>
public interface IRecycleBin
{
    /// <summary>Returns true when the file was moved to the Recycle Bin (or no longer exists).</summary>
    bool Send(string path);
}
