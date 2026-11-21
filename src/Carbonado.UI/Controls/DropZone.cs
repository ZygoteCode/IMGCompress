using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Carbonado.UI.Controls;

/// <summary>
/// Drop target for files and folders (Explorer file drops only). While files hover over it
/// <see cref="IsDragOver"/> is true and the template moves to its <c>DragOver</c> visual state (prismatic dashed
/// edge that marches, inner glow); dropping raises <see cref="FilesDropped"/> with the paths.
/// </summary>
public class DropZone : ContentControl
{
    private static readonly DependencyPropertyKey IsDragOverPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsDragOver), typeof(bool), typeof(DropZone), new FrameworkPropertyMetadata(false, OnIsDragOverChanged));

    public static readonly DependencyProperty IsDragOverProperty = IsDragOverPropertyKey.DependencyProperty;

    private bool _leavePending;

    static DropZone()
    {
        AllowDropProperty.OverrideMetadata(typeof(DropZone), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Raised with the dropped file and folder paths.</summary>
    public event EventHandler<FilesDroppedEventArgs>? FilesDropped;

    /// <summary>True while a file drag is over the zone.</summary>
    public bool IsDragOver
    {
        get => (bool)GetValue(IsDragOverProperty);
        private set => SetValue(IsDragOverPropertyKey, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateDragState(false);
    }

    /// <summary>Accepts <paramref name="data"/> if it carries file paths: raises <see cref="FilesDropped"/>.</summary>
    /// <returns>True when files were accepted.</returns>
    public bool AcceptDrop(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _leavePending = false;
        IsDragOver = false;
        var paths = Paths(data);
        if (paths is not { Length: > 0 })
        {
            return false;
        }

        FilesDropped?.Invoke(this, new FilesDroppedEventArgs(paths));
        return true;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        Track(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        Track(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);

        // Moving between the zone and its children raises Leave then Enter in the same input pass: only a Leave
        // that no Enter follows really leaves, so the visual state does not flicker.
        _leavePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_leavePending)
            {
                _leavePending = false;
                IsDragOver = false;
            }
        });
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Effects = AcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Track(DragEventArgs e)
    {
        _leavePending = false;
        bool files = HasFiles(e.Data);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        IsDragOver = files;
        e.Handled = true;
    }

    /// <summary>Whether <paramref name="data"/> offers file paths (cheap: no data is rendered while dragging).</summary>
    private static bool HasFiles(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop);
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>File paths carried by <paramref name="data"/>, or null (also for COM/virtual data that fails to render).</summary>
    private static string[]? Paths(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    private void UpdateDragState(bool useTransitions) =>
        VisualStateManager.GoToState(this, IsDragOver ? "DragOver" : "Idle", useTransitions);

    private static void OnIsDragOverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DropZone)d).UpdateDragState(true);
}
