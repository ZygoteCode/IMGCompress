using System.Collections.Concurrent;
using Carbonado.Core.Analysis;
using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Encoders;

/// <summary>
/// The source image as every encoder sees it: final pixels (upright, cleaned, resized), policy-filtered metadata, the
/// analysis, and lazily written intermediates in a private work directory. Each intermediate is written at most once,
/// on first request, and is safe to request concurrently. Tools receive short ASCII file names relative to
/// <see cref="WorkDir"/>, so Unicode or very long user paths never reach them.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> may run while intermediates are still being written (e.g. after a caller cancelled its wait):
/// it refuses new requests, waits for the writes already started (derived intermediates are cancelled), and only then
/// frees the pixels and deletes the files, so no write can outlive the buffer it reads or leave a file behind.
/// </remarks>
public sealed class PreparedSource : IDisposable
{
    /// <summary>File names of the standard intermediates inside <see cref="WorkDir"/>.</summary>
    public const string PngName = "src.png", PamName = "src.pam", IccName = "src.icc", ExifName = "src.exif", XmpName = "src.xmp";

    // Every creation is started under _gate while not disposed, so Dispose (which flips _disposed under the same lock)
    // sees all of them. Entries are removed lock-free when a creation fails, so the next caller retries.
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _writes;
    private bool _disposed;

    private PreparedSource(string originalPath, ImageFormat format, PixelBuffer pixels, ImageMetadata metadata, ImageAnalysis analysis, string workDir, int frameCount, bool preserveInvisibleRgb)
    {
        OriginalPath = originalPath;
        OriginalFormat = format;
        Pixels = pixels;
        Metadata = metadata;
        Analysis = analysis;
        WorkDir = workDir;
        FrameCount = Math.Max(1, frameCount);
        PreserveInvisibleRgb = preserveInvisibleRgb;
    }

    /// <summary>The user's file (never modified).</summary>
    public string OriginalPath { get; }

    public ImageFormat OriginalFormat { get; }

    /// <summary>The pixels to encode (owned by this instance).</summary>
    public PixelBuffer Pixels { get; }

    /// <summary>The metadata to embed, already filtered by the metadata policy, with EXIF orientation 1 (pixels are upright).</summary>
    public ImageMetadata Metadata { get; }

    public ImageAnalysis Analysis { get; }

    /// <summary>Private working directory for intermediates and temporary outputs.</summary>
    public string WorkDir { get; }

    /// <summary>Frames in the original (> 1 for animations, which encoders take from the original file).</summary>
    public int FrameCount { get; }

    public bool IsAnimated => FrameCount > 1;

    /// <summary>
    /// The RGB under fully transparent pixels must be kept exactly (the profile forbids invisible-pixel cleanup);
    /// encoders then disable their own invisible-pixel optimizations.
    /// </summary>
    public bool PreserveInvisibleRgb { get; }

    /// <summary>Number of intermediates created so far (diagnostics and tests).</summary>
    internal int IntermediatesCreated => Volatile.Read(ref _writes);

    /// <summary>Creates a prepared source, taking ownership of <paramref name="pixels"/>.</summary>
    /// <param name="frameCount">Frames in the original file (animations are encoded from the original).</param>
    /// <param name="preserveInvisibleRgb">See <see cref="PreserveInvisibleRgb"/>.</param>
    public static PreparedSource Create(string originalPath, ImageFormat format, PixelBuffer pixels, ImageMetadata metadata, ImageAnalysis analysis, string workDir, int frameCount = 1, bool preserveInvisibleRgb = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrEmpty(workDir);
        Directory.CreateDirectory(workDir);
        return new PreparedSource(originalPath, format, pixels, metadata, analysis, Path.GetFullPath(workDir), frameCount, preserveInvisibleRgb);
    }

    /// <summary>The pixels as a fast PNG carrying all metadata (ICC, EXIF, XMP, DPI). Returns the full path.</summary>
    public Task<string> GetPngAsync(CancellationToken ct) =>
        GetOrCreateAsync(PngName, path => WriteFile(path, s => PngEncoder.EncodeFast(s, Pixels, Metadata)), ct);

    /// <summary>
    /// The pixels as PAM (no metadata; alpha omitted when opaque; gray when R = G = B and no RGB profile applies).
    /// Returns the full path.
    /// </summary>
    public Task<string> GetPamAsync(CancellationToken ct) =>
        GetOrCreateAsync(PamName, path => WriteFile(path, s => PnmCodec.WritePam(s, Pixels, allowGray: !IccHeader.IsRgb(Metadata.Icc))), ct);

    /// <summary>
    /// The pixels as a fast 8-bit PNG (16-bit sources rounded) without any metadata: the input for encoders that are
    /// 8-bit only or that would misinterpret embedded profiles (cwebp, pngquant). Returns the full path.
    /// </summary>
    internal Task<string> GetBarePng8Async(CancellationToken ct) =>
        GetOrCreateAsync("src8-bare.png", path =>
        {
            using var eight = Pixels.Depth == SampleDepth.Eight ? null : Pixels.ToDepth(SampleDepth.Eight);
            return WriteFile(path, s => PngEncoder.EncodeFast(s, eight ?? Pixels, null));
        }, ct);

    /// <summary>The ICC profile as a file, or null when there is none.</summary>
    public Task<string?> GetIccPathAsync() => MetadataFileAsync(IccName, Metadata.Icc);

    /// <summary>The EXIF payload (TIFF structure) as a file, or null when there is none.</summary>
    public Task<string?> GetExifPathAsync() => MetadataFileAsync(ExifName, Metadata.Exif);

    /// <summary>The XMP packet as a file, or null when there is none.</summary>
    public Task<string?> GetXmpPathAsync() => MetadataFileAsync(XmpName, Metadata.Xmp);

    /// <summary>
    /// The JPEG whose DCT data encoders must keep (JPEG → JPEG XL transcode, JPEG optimization), and the reference
    /// a JPEG XL reconstruction is verified against: the original file itself when the metadata is unchanged, otherwise
    /// a copy whose metadata segments follow <see cref="Metadata"/> (the original EXIF orientation is kept, because the
    /// coded data is not rotated). Only for JPEG sources.
    /// </summary>
    public Task<string> GetJpegReferenceAsync(CancellationToken ct)
    {
        if (OriginalFormat != ImageFormat.Jpeg)
        {
            throw new InvalidOperationException("The source is not a JPEG.");
        }

        return GetOrCreateAsync("reference.jpg", WriteJpegReferenceAsync, ct);
    }

    /// <summary>
    /// A derived intermediate (e.g. an adapter-specific input) created once by <paramref name="create"/>, which receives
    /// the full path to write. Concurrent callers share one creation; a failed creation is retried by the next caller.
    /// The token given to <paramref name="create"/> is cancelled by <see cref="Dispose"/> (never by a caller's token).
    /// </summary>
    internal Task<string> GetDerivedAsync(string fileName, Func<string, CancellationToken, Task> create, CancellationToken ct) =>
        GetOrCreateAsync(fileName, async path =>
        {
            await create(path, _lifetime.Token).ConfigureAwait(false);
            return path;
        }, ct);

    /// <summary>
    /// For an animated PNG source: the original APNG, every frame untouched, whose metadata is exactly
    /// <see cref="Metadata"/> (so the metadata policy holds for animations too; text, time and private chunks are
    /// removed). The EXIF orientation stored in the original is kept, because the frames are not rotated.
    /// </summary>
    internal Task<string> GetAnimatedPngAsync(CancellationToken ct)
    {
        if (OriginalFormat != ImageFormat.Apng)
        {
            throw new InvalidOperationException("The source is not an animated PNG.");
        }

        return GetOrCreateAsync("original-filtered.png", WriteAnimatedPngAsync, ct);
    }

    /// <summary>A copy of the original file inside <see cref="WorkDir"/> under a short ASCII name.</summary>
    internal Task<string> GetOriginalCopyAsync(CancellationToken ct) =>
        GetOrCreateAsync("original" + Path.GetExtension(OriginalPath).ToLowerInvariant(), path =>
        {
            File.Copy(OriginalPath, path, overwrite: true);
            return Task.FromResult(path);
        }, ct);

    /// <summary>
    /// Releases the pixels and deletes the intermediates written by this instance, after waiting for the writes still
    /// in progress; later requests throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        Task<string>[] started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = [.. _files.Values.Where(l => l.IsValueCreated).Select(l => l.Value)];
        }

        // Derived creations may run tools: stop them. Built-in writes are short and finish on their own.
        _lifetime.Cancel();
        try
        {
            Task.WaitAll(started);
        }
        catch (AggregateException)
        {
            // Failures were already reported to (or abandoned by) the callers; only completion matters here.
        }

        Pixels.Dispose();
        foreach (Task<string> task in started)
        {
            if (task.IsCompletedSuccessfully && !IsOriginal(task.Result))
            {
                TryDelete(task.Result);
            }
        }

        _lifetime.Dispose();
    }

    private bool IsOriginal(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(OriginalPath), StringComparison.OrdinalIgnoreCase);

    private async Task<string?> MetadataFileAsync(string name, byte[]? data) =>
        data is { Length: > 0 }
            ? await GetOrCreateAsync(name, async path =>
            {
                await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
                return path;
            }, CancellationToken.None).ConfigureAwait(false)
            : null;

    // `create` receives the path to write and returns the path of the result (normally the same).
    private Task<string> GetOrCreateAsync(string name, Func<string, Task<string>> create, CancellationToken ct)
    {
        Task<string> creation;
        lock (_gate)
        {
            // Checked and started under the lock: Dispose either sees this creation (and waits for it) or it never starts.
            ObjectDisposedException.ThrowIf(_disposed, this);
            string path = Path.Combine(WorkDir, name);
            creation = _files.GetOrAdd(name, _ => new Lazy<Task<string>>(() => CreateAsync(name, path, create))).Value;
        }

        return creation.WaitAsync(ct);
    }

    private async Task<string> CreateAsync(string name, string path, Func<string, Task<string>> create)
    {
        try
        {
            string result = await Task.Run(() => create(path)).ConfigureAwait(false);
            Interlocked.Increment(ref _writes);
            return result;
        }
        catch
        {
            // Let the next caller retry instead of caching the failure. The partial file goes first: while the entry
            // exists no retry can start writing the same path.
            TryDelete(path);
            _files.TryRemove(name, out _);
            throw;
        }
    }

    private static Task<string> WriteFile(string path, Action<Stream> write)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            write(fs);
        }

        return Task.FromResult(path);
    }

    private async Task<string> WriteJpegReferenceAsync(string path)
    {
        byte[] original = await File.ReadAllBytesAsync(OriginalPath).ConfigureAwait(false);
        JpegSegments.Metadata existing;
        using (var ms = new MemoryStream(original, writable: false))
        {
            existing = JpegSegments.Read(ms);
        }

        ushort orientation = existing.Exif is null ? (ushort)1 : ExifReader.ReadOrientation(existing.Exif);
        byte[]? exif = Metadata.Exif is { } kept
            ? ExifReader.WithOrientation(kept, orientation)
            : orientation != 1 ? ExifReader.CreateMinimal(orientation) : null;

        // Comments, IPTC and similar segments travel with EXIF/XMP: kept by the policies that keep those, dropped otherwise.
        bool keepOther = Metadata.Exif is not null || Metadata.Xmp is not null;
        bool unchanged = SameBytes(exif, existing.Exif) && SameBytes(Metadata.Xmp, existing.Xmp) && SameBytes(Metadata.Icc, existing.Icc)
            && (keepOther || !existing.HasOtherMetadata);
        if (unchanged)
        {
            // Nothing to change: the original itself is the reference.
            return OriginalPath;
        }

        await File.WriteAllBytesAsync(path, JpegSegments.Rewrite(original, exif, Metadata.Xmp, Metadata.Icc, keepOther)).ConfigureAwait(false);
        return path;
    }

    private async Task<string> WriteAnimatedPngAsync(string path)
    {
        byte[] original = await File.ReadAllBytesAsync(OriginalPath).ConfigureAwait(false);
        ImageMetadata stored;
        using (var ms = new MemoryStream(original, writable: false))
        {
            stored = PngDecoder.ReadMetadata(ms);
        }

        ushort orientation = stored.Exif is null ? (ushort)1 : ExifReader.ReadOrientation(stored.Exif);
        ImageMetadata metadata = Metadata.Clone();
        metadata.Orientation = orientation;
        metadata.Exif = Metadata.Exif is { } kept
            ? ExifReader.WithOrientation(kept, orientation)
            : orientation != 1 ? ExifReader.CreateMinimal(orientation) : null;
        await File.WriteAllBytesAsync(path, PngEncoder.ReplaceMetadata(original, metadata, dropUnknownChunks: true)).ConfigureAwait(false);
        return path;
    }

    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
