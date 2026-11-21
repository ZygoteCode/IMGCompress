namespace Carbonado.UI.Controls;

/// <summary>Files and folders dropped on a <see cref="DropZone"/>, in the order Explorer supplied them.</summary>
public sealed class FilesDroppedEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    /// <summary>Absolute paths of the dropped files and folders (folders are not expanded).</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
}
