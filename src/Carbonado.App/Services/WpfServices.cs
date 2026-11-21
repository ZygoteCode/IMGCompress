using System.Diagnostics;
using System.Windows.Threading;
using Carbonado.Core.Logging;

namespace Carbonado.App.Services;

/// <summary><see cref="IUiDispatcher"/> over a WPF dispatcher (Background priority keeps input responsive).</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(DispatcherPriority.Background, action);
}

/// <summary>Opens folders/files/URLs through the Windows shell.</summary>
public sealed class ShellLauncher : IShellLauncher
{
    public void OpenFolder(string folder, string? selectFile = null)
    {
        try
        {
            if (selectFile is not null && File.Exists(selectFile))
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/select," + selectFile);
                Process.Start(psi)?.Dispose();
            }
            else if (Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not open folder {folder}: {e.Message}");
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not open {path}: {e.Message}");
        }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not open {url}: {e.Message}");
        }
    }
}
