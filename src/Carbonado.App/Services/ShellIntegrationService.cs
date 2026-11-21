using Carbonado.Core.Logging;
using Microsoft.Win32;

namespace Carbonado.App.Services;

/// <summary>
/// Adds/removes "Compress with Carbonado" to the Explorer context menu of images and folders, per user (HKCU,
/// no admin rights). Multi-selection launches one process per item; the single-instance pipe gathers them.
/// </summary>
public sealed class ShellIntegrationService
{
    internal const string ImageKey = @"Software\Classes\SystemFileAssociations\image\shell\Carbonado";
    internal const string FolderKey = @"Software\Classes\Directory\shell\Carbonado";
    private readonly RegistryKey _root;
    private readonly string _prefix;

    /// <param name="root">Registry root (HKCU in production; tests pass a scratch key).</param>
    /// <param name="prefix">Optional sub-path prepended to the key paths (tests).</param>
    public ShellIntegrationService(RegistryKey? root = null, string prefix = "")
    {
        _root = root ?? Registry.CurrentUser;
        _prefix = prefix;
    }

    public bool IsRegistered
    {
        get
        {
            using var key = _root.OpenSubKey(Path.Combine(_prefix, ImageKey, "command"));
            return key?.GetValue(null) is string;
        }
    }

    /// <summary>Registers the verb for <paramref name="exePath"/> with the localized <paramref name="label"/>.</summary>
    public void Register(string exePath, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        foreach (string sub in new[] { ImageKey, FolderKey })
        {
            using var verb = _root.CreateSubKey(Path.Combine(_prefix, sub));
            verb.SetValue(null, label);
            verb.SetValue("Icon", $"\"{exePath}\",0");
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{exePath}\" \"%1\"");
        }

        Log.Info("Explorer integration registered.");
    }

    public void Unregister()
    {
        foreach (string sub in new[] { ImageKey, FolderKey })
        {
            _root.DeleteSubKeyTree(Path.Combine(_prefix, sub), throwOnMissingSubKey: false);
        }

        Log.Info("Explorer integration removed.");
    }
}
