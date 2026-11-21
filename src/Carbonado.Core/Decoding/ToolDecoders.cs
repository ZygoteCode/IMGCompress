using System.Globalization;
using System.Text.RegularExpressions;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Decoding;

/// <summary>
/// Decoders backed by external tools, run through <see cref="ProcessRunner"/> with temporary files in a private work
/// directory: dwebp (WebP → PAM), avifdec (AVIF → PNG), djxl (JPEG XL → PNG with metadata, or PAM for outputs) and
/// ffmpeg (everything else → raw RGBA, 8- or 16-bit following the source).
/// </summary>
internal sealed partial class ToolDecoders(ToolLocator tools, ProcessRunner runner, string workDir)
{
    // A decoder should never take this long; the limit only guards against tools that hang.
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(5);

    // Paths at least this long are copied into the work directory first (tools are not all long-path aware).
    private const int MaxToolPathLength = 240;

    /// <summary>WebP via dwebp; animations decode their first frame (repackaged as a still) on the canvas.</summary>
    public async Task<DecodedImage> WebPAsync(string path, long maxPixels, CancellationToken ct)
    {
        string dwebp = Require(ToolId.Dwebp, "WebP");
        var info = WebPContainer.Read(path);
        string? still = null;
        string input = path;
        if (info.Frames > 0)
        {
            if (info.FirstFrame is null)
            {
                throw new InvalidDataException("WebP: the animation has no decodable first frame.");
            }

            // The canvas size comes from the VP8X header alone: capped before anything is decoded or allocated.
            DecodeLimits.CheckPixels(info.CanvasWidth, info.CanvasHeight, maxPixels, "WebP");

            still = TempPath(".webp");
            await File.WriteAllBytesAsync(still, info.FirstFrame.StillWebP, ct).ConfigureAwait(false);
            input = still;
        }

        string output = TempPath(".pam");
        try
        {
            using (var tin = ToolInput.For(input, workDir))
            {
                await RunAsync(dwebp, ["-quiet", "-mt", tin.Path, "-pam", "-o", output], "WebP", ct).ConfigureAwait(false);
            }

            using var decoded = DecodePamFile(output, maxPixels);
            PixelBuffer pixels = info.FirstFrame is { } frame && info.CanvasWidth > 0
                ? PlaceOnCanvas(decoded.DetachPixels(), info.CanvasWidth, info.CanvasHeight, frame.X, frame.Y)
                : decoded.DetachPixels();
            var metadata = new ImageMetadata { Icc = info.Icc, Exif = info.Exif, Xmp = info.Xmp };
            return new DecodedImage(pixels, metadata, ImageFormat.WebP, Math.Max(1, info.Frames), 8);
        }
        finally
        {
            TryDelete(output);
            TryDelete(still);
        }
    }

    /// <summary>AVIF via avifdec to an uncompressed PNG (16-bit when the AVIF has more than 8 bits); irot/imir are applied by avifdec.</summary>
    public async Task<DecodedImage> AvifAsync(string path, long maxPixels, CancellationToken ct)
    {
        string avifdec = Require(ToolId.Avifdec, "AVIF");
        int depth = AvifInfo.BitDepth(path);
        string output = TempPath(".png");
        using var input = ToolInput.For(path, workDir);
        try
        {
            List<string> args = ["--png-compress", "0"];
            if (depth > 8)
            {
                args.AddRange(["-d", "16"]);
            }

            args.AddRange(["--", input.Path, output]);
            await RunAsync(avifdec, args, "AVIF", ct).ConfigureAwait(false);
            using var png = DecodePngFile(output, maxPixels, ct);
            return new DecodedImage(png.DetachPixels(), png.Metadata, ImageFormat.Avif, 1, depth);
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// JPEG XL via djxl (codestream orientation applied). With <paramref name="withMetadata"/> the output is PNG so
    /// the ICC/EXIF/XMP survive; otherwise PAM (faster, for verifying Carbonado's own outputs).
    /// </summary>
    public async Task<DecodedImage> JxlAsync(string path, bool withMetadata, long maxPixels, CancellationToken ct)
    {
        string djxl = Require(ToolId.Djxl, "JPEG XL");
        string output = TempPath(withMetadata ? ".png" : ".pam");
        using var input = ToolInput.For(path, workDir);
        try
        {
            await RunAsync(djxl, [input.Path, output, "--quiet", "--bits_per_sample=-1"], "JPEG XL", ct).ConfigureAwait(false);
            using var decoded = withMetadata ? DecodePngFile(output, maxPixels, ct) : DecodePamFile(output, maxPixels);
            return new DecodedImage(decoded.DetachPixels(), decoded.Metadata, ImageFormat.Jxl, decoded.FrameCount, decoded.SignificantBits);
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// Any other format (HEIC, QOI, PSD, EXR, HDR, JPEG 2000, TGA, …) via ffmpeg (first frame). A probe run reads the
    /// input pixel format first, so 8-bit sources are decoded as 8-bit RGBA (exact) and deeper ones as 16-bit.
    /// </summary>
    public async Task<DecodedImage> FfmpegAsync(string path, ImageFormat format, long maxPixels, CancellationToken ct)
    {
        string ffmpeg = Require(ToolId.Ffmpeg, format.ToString());
        string output = TempPath(".raw");
        using var input = ToolInput.For(path, workDir);
        try
        {
            // Without an output file ffmpeg only prints the input streams (and exits with an error by design).
            var probe = await runner.RunAsync(new ProcessSpec(ffmpeg, ["-hide_banner", "-nostdin", "-i", input.Path], workDir, DecodeTimeout), ct).ConfigureAwait(false);
            var probed = ParseFfmpegStreams(probe.Stderr);
            if (!probed.HasVideoInput)
            {
                throw new InvalidDataException($"{format}: ffmpeg could not read the file ({Summarize(probe)}).");
            }

            // Refused before ffmpeg writes (and Carbonado allocates) a raster of the size the header claims.
            if (ParseFfmpegInputSize(probe.Stderr) is { Width: > 0, Height: > 0 } declared)
            {
                DecodeLimits.CheckPixels(declared.Width, declared.Height, maxPixels, format.ToString());
            }

            bool deep = probed.HighDepth;
            var result = await runner.RunAsync(
                new ProcessSpec(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "info", "-i", input.Path, "-map", "0:v:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", deep ? "rgba64le" : "rgba", "-y", output], workDir, DecodeTimeout),
                ct).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidDataException($"{format}: ffmpeg could not decode the file ({Summarize(result)}).");
            }

            var (width, height, _, _) = ParseFfmpegStreams(result.Stderr);
            int bytesPerPixel = deep ? 8 : 4;
            long length = new FileInfo(output).Length;
            if (width <= 0 || height <= 0 || length != (long)width * height * bytesPerPixel)
            {
                throw new InvalidDataException($"{format}: unexpected ffmpeg output ({width}×{height}, {length} bytes).");
            }

            DecodeLimits.CheckPixels(width, height, maxPixels, format.ToString());
            var pixels = DecodeLimits.Allocate(width, height, deep ? SampleDepth.Sixteen : SampleDepth.Eight, clear: false, format.ToString());
            try
            {
                using var raw = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                for (int y = 0; y < height; y++)
                {
                    raw.ReadExactly(pixels.RowBytes(y));
                }

                return new DecodedImage(pixels, new ImageMetadata(), format, 1, deep ? 16 : 8);
            }
            catch
            {
                pixels.Dispose();
                throw;
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// Output size (from the Output section), whether the input pixel format has more than 8 bits, and whether an
    /// input video stream was reported at all.
    /// </summary>
    internal static (int Width, int Height, bool HighDepth, bool HasVideoInput) ParseFfmpegStreams(string stderr)
    {
        int output = stderr.IndexOf("Output #0", StringComparison.Ordinal);
        int input = stderr.IndexOf("Input #0", StringComparison.Ordinal);
        int width = 0, height = 0;
        bool high = false, hasInput = false;
        if (output >= 0 && VideoLine(stderr, output) is { } outLine && SizePattern().Match(outLine) is { Success: true } size)
        {
            width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
            height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        if (input >= 0 && VideoLine(stderr, input) is { } inLine)
        {
            hasInput = true;
            if (PixelFormatPattern().Match(inLine) is { Success: true } pixFmt)
            {
                high = HighDepthPattern().IsMatch(pixFmt.Groups[1].Value);
            }
        }

        return (width, height, high, hasInput);

        static string? VideoLine(string text, int from)
        {
            int video = text.IndexOf("Video:", from, StringComparison.Ordinal);
            if (video < 0)
            {
                return null;
            }

            int end = text.IndexOf('\n', video);
            return text[video..(end < 0 ? text.Length : end)];
        }
    }

    /// <summary>The size of the first input video stream (from the Input section), or 0×0 when none is reported.</summary>
    internal static (int Width, int Height) ParseFfmpegInputSize(string stderr)
    {
        int input = stderr.IndexOf("Input #0", StringComparison.Ordinal);
        int output = stderr.IndexOf("Output #0", StringComparison.Ordinal);
        int video = input < 0 ? -1 : stderr.IndexOf("Video:", input, StringComparison.Ordinal);
        if (video < 0 || (output >= 0 && video > output))
        {
            return (0, 0);
        }

        int end = stderr.IndexOf('\n', video);
        return SizePattern().Match(stderr[video..(end < 0 ? stderr.Length : end)]) is { Success: true } size
            ? (int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    [GeneratedRegex(@"(?<=[\s,])(\d{1,6})x(\d{1,6})(?=[\s,\[]|$)")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"Video: [^,]+, ([a-z0-9_]+)")]
    private static partial Regex PixelFormatPattern();

    [GeneratedRegex(@"p(9|10|12|14|16)|p0(10|12|16)|(9|10|12|14|16)(le|be)$|48|64|f16|f32")]
    private static partial Regex HighDepthPattern();

    private static PixelBuffer PlaceOnCanvas(PixelBuffer frame, int canvasW, int canvasH, int x, int y)
    {
        if (frame.Width == canvasW && frame.Height == canvasH && x == 0 && y == 0)
        {
            return frame;
        }

        PixelBuffer canvas;
        try
        {
            canvas = DecodeLimits.Allocate(canvasW, canvasH, frame.Depth, clear: true, "WebP");
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        for (int row = 0; row < frame.Height && y + row < canvasH; row++)
        {
            int count = Math.Min(frame.Width, canvasW - x);
            if (count > 0)
            {
                frame.RowBytes(row)[..(count * frame.BytesPerPixel)].CopyTo(canvas.RowBytes(y + row)[(x * frame.BytesPerPixel)..]);
            }
        }

        frame.Dispose();
        return canvas;
    }

    private static DecodedImage DecodePngFile(string path, long maxPixels, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return PngDecoder.Decode(fs, maxPixels, ct);
    }

    private static DecodedImage DecodePamFile(string path, long maxPixels)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return PnmCodec.Decode(fs, maxPixels);
    }

    private static string Summarize(ProcessResult r)
    {
        if (r.TimedOut)
        {
            return "timed out";
        }

        string[] lines = r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string tail = string.Join(" ", lines.TakeLast(3));
        return tail.Length == 0 ? $"exit code {r.ExitCode}" : tail.Length > 300 ? tail[^300..] : tail;
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

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

    private string Require(ToolId id, string what) =>
        tools.Get(id)?.Path ?? throw new NotSupportedException($"Decoding {what} needs {ToolLocator.Layout(id).FileName}, which is not installed.");

    private string TempPath(string extension) => Path.Combine(workDir, $"dec-{Guid.NewGuid():N}{extension}");

    private async Task RunAsync(string exe, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await runner.RunAsync(new ProcessSpec(exe, args, workDir, DecodeTimeout), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidDataException($"{what}: the file could not be decoded ({Summarize(result)}).");
        }
    }

    /// <summary>A path safe to hand to a tool: the original, or a short-named copy in the work directory for very long paths.</summary>
    private sealed class ToolInput : IDisposable
    {
        private readonly string? _copy;

        private ToolInput(string path, string? copy)
        {
            Path = path;
            _copy = copy;
        }

        public string Path { get; }

        public static ToolInput For(string path, string workDir)
        {
            string full = System.IO.Path.GetFullPath(path);
            if (full.Length < MaxToolPathLength)
            {
                return new ToolInput(full, null);
            }

            string copy = System.IO.Path.Combine(workDir, $"in-{Guid.NewGuid():N}{System.IO.Path.GetExtension(full)}");
            File.Copy(full, copy);
            return new ToolInput(copy, copy);
        }

        public void Dispose() => TryDelete(_copy);
    }
}
