using System.Windows.Media;
using Carbonado.Core.Engine;

namespace Carbonado.App.Services;

/// <summary>Marshals work to the UI thread (abstracted so view-models are testable without WPF).</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);
}

/// <summary>Basic facts and a small preview of an input image.</summary>
public sealed record ThumbnailInfo(ImageSource? Image, int Width, int Height, long Bytes);

public interface IThumbnailService
{
    /// <summary>Decodes a small preview (≤ <paramref name="maxSize"/> px) off the UI thread; null if unreadable.</summary>
    Task<ThumbnailInfo?> LoadAsync(string path, int maxSize, CancellationToken ct);
}

/// <summary>Built-in + custom profiles and the active selection.</summary>
public interface IProfileCatalog
{
    IReadOnlyList<CompressionProfile> All { get; }

    CompressionProfile Active { get; }

    event EventHandler? Changed;

    CompressionProfile? Find(string id);

    void SetActive(string id);

    /// <summary>Adds or replaces a custom profile (built-ins cannot be overwritten).</summary>
    void SaveCustom(CompressionProfile profile);

    void DeleteCustom(string id);
}

public enum ConfirmResult
{
    Primary,
    Secondary,
    Cancel,
}

public interface IDialogService
{
    Task<ConfirmResult> ConfirmAsync(string title, string message, string primary, string? secondary = null, bool danger = false);

    IReadOnlyList<string> PickFiles();

    string? PickFolder(string? initial = null);
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public interface IToastService
{
    void Show(string title, string message, ToastKind kind = ToastKind.Info);
}

public interface ISoundService
{
    void PlayCompletion();
}

/// <summary>Opens files/folders in the shell.</summary>
public interface IShellLauncher
{
    void OpenFolder(string folder, string? selectFile = null);

    void OpenFile(string path);

    void OpenUrl(string url);
}
