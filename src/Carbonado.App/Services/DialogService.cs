using System.ComponentModel;
using System.Windows;
using Carbonado.App.Localization;
using Carbonado.App.Shell;
using Carbonado.Core.Imaging;
using Carbonado.UI.Controls;
using Microsoft.Win32;

namespace Carbonado.App.Services;

/// <summary>
/// In-window modal confirmations (brand ModalHost, keyboard-trapped, Esc = cancel) and native file/folder pickers.
/// </summary>
public sealed class DialogService(ModalHost host, Window owner) : IDialogService
{
    private static readonly DependencyPropertyDescriptor s_isOpen =
        DependencyPropertyDescriptor.FromProperty(ModalHost.IsOpenProperty, typeof(ModalHost));

    public Task<ConfirmResult> ConfirmAsync(string title, string message, string primary, string? secondary = null, bool danger = false)
    {
        var tcs = new TaskCompletionSource<ConfirmResult>();
        var dialog = new ConfirmDialog(title, message, primary, secondary, danger);
        EventHandler? closedByHost = null;

        void Finish(ConfirmResult result)
        {
            s_isOpen.RemoveValueChanged(host, closedByHost);
            if (tcs.TrySetResult(result))
            {
                host.IsOpen = false;
                host.Dialog = null;
            }
        }

        closedByHost = (_, _) =>
        {
            if (!host.IsOpen)
            {
                Finish(ConfirmResult.Cancel);
            }
        };
        dialog.Answered += (_, result) => Finish(result);
        host.Dialog = dialog;
        host.IsOpen = true;
        s_isOpen.AddValueChanged(host, closedByHost);
        return tcs.Task;
    }

    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.Get("L.Forge.AddFiles"),
            Filter = $"{Loc.Get("L.Forge.Drop.Title")}|{string.Join(';', FormatInfo.InputExtensions.Select(e => "*" + e))}|*.*|*.*",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = Loc.Get("L.Forge.AddFolder") };
        if (initial is not null && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}

/// <summary>Toasts in the window's ToastHost (UI thread), honouring the "show notifications" setting.</summary>
public sealed class ToastService(ToastHost host, Func<bool> enabled) : IToastService
{
    public void Show(string title, string message, ToastKind kind = ToastKind.Info)
    {
        if (!enabled() && kind is not (ToastKind.Error or ToastKind.Warning))
        {
            return;
        }

        void ShowNow() => host.Show(title, message, kind switch
        {
            ToastKind.Success => UI.Controls.ToastKind.Success,
            ToastKind.Warning => UI.Controls.ToastKind.Warning,
            ToastKind.Error => UI.Controls.ToastKind.Error,
            _ => UI.Controls.ToastKind.Info,
        });

        if (host.Dispatcher.CheckAccess())
        {
            ShowNow();
        }
        else
        {
            host.Dispatcher.BeginInvoke(ShowNow);
        }
    }
}
