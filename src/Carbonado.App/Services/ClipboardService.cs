using System.Windows;
using System.Windows.Media.Imaging;
using Carbonado.Core.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.App.Services;

/// <summary>
/// Turns clipboard content into files Carbonado can queue: copied files/folders are used as they are; a copied
/// bitmap (screenshot, image from a browser) is saved losslessly as PNG into <c>Pictures\Carbonado\Pasted</c>.
/// Must be called on the UI (STA) thread.
/// </summary>
public sealed class ClipboardService
{
    private readonly string _pasteFolder;

    public ClipboardService(string? pasteFolder = null)
    {
        _pasteFolder = pasteFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Carbonado", "Pasted");
    }

    public bool HasContent()
    {
        try
        {
            return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Paths to queue (empty when the clipboard holds nothing usable).</summary>
    public IReadOnlyList<string> GetPaths()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                return [.. Clipboard.GetFileDropList().Cast<string>().Where(p => Directory.Exists(p) || FormatInfo.IsInputExtension(p))];
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                return [SaveImage(image, _pasteFolder, DateTime.Now)];
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Paste failed: {e.Message}");
        }

        return [];
    }

    /// <summary>Saves <paramref name="image"/> as a PNG named after <paramref name="when"/> (unique) and returns the path.</summary>
    internal static string SaveImage(BitmapSource image, string folder, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string stem = $"pasted-{when:yyyyMMdd-HHmmss}";
        string path = Path.Combine(folder, stem + ".png");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem}-{n}.png");
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(fs);
        return path;
    }
}
