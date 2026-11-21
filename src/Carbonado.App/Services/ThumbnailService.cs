using System.Windows.Media.Imaging;
using Carbonado.Core.Logging;

namespace Carbonado.App.Services;

/// <summary>
/// Small previews through WIC on thread-pool threads (decoder-side downscaling via DecodePixelWidth, frozen
/// bitmaps). Formats WIC cannot read (e.g. JXL/AVIF without Windows extensions) return size facts only.
/// </summary>
public sealed class ThumbnailService : IThumbnailService
{
    public Task<ThumbnailInfo?> LoadAsync(string path, int maxSize, CancellationToken ct) =>
        Task.Run(() => Load(path, maxSize), ct);

    private static ThumbnailInfo? Load(string path, int maxSize)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            int width = frame.PixelWidth, height = frame.PixelHeight;
            stream.Position = 0;

            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            thumb.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            thumb.StreamSource = stream;
            if (width >= height)
            {
                thumb.DecodePixelWidth = Math.Min(maxSize, width);
            }
            else
            {
                thumb.DecodePixelHeight = Math.Min(maxSize, height);
            }

            thumb.EndInit();
            thumb.Freeze();
            return new ThumbnailInfo(thumb, width, height, bytes);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"No WIC preview for {Path.GetFileName(path)}: {e.GetType().Name}");
            return new ThumbnailInfo(null, 0, 0, bytes);
        }
    }
}
