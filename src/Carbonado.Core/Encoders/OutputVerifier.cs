using System.Security.Cryptography;
using System.Text;
using Carbonado.Core.Decoding;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders;

/// <summary>Verifies outputs: decodes them like a viewer would, and proves JPEG → JPEG XL transcodes reversible.</summary>
public sealed class OutputVerifier
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly string? _workDir;

    /// <param name="loader">Decodes outputs for verification.</param>
    /// <param name="tools">Locates djxl.</param>
    /// <param name="runner">Runs it.</param>
    /// <param name="workDir">
    /// Private directory (e.g. the session temp directory) for the reconstructed JPEG; a JPEG XL file outside it, or
    /// with a name a tool may not open (non-ASCII), is copied there first. When null, the reconstruction is written
    /// next to the JPEG XL file.
    /// </param>
    public OutputVerifier(IImageLoader loader, ToolLocator tools, ProcessRunner runner, string? workDir = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runner);
        Loader = loader;
        _tools = tools;
        _runner = runner;
        _workDir = workDir is null ? null : Path.GetFullPath(workDir);
    }

    /// <summary>The decoder used for pixel and perceptual verification of outputs (<see cref="IImageLoader.DecodeOutputAsync"/>).</summary>
    public IImageLoader Loader { get; }

    /// <summary>
    /// True when djxl reconstructs a JPEG from <paramref name="jxlPath"/> that is byte-for-byte identical (SHA-256) to
    /// <paramref name="originalJpegPath"/>. For outputs of <see cref="EncodeKind.JpegTranscode"/>, pass
    /// <see cref="PreparedSource.GetJpegReferenceAsync"/>: it is the original file whenever the metadata policy left it unchanged.
    /// </summary>
    public async Task<bool> VerifyJpegReconstructionAsync(string jxlPath, string originalJpegPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jxlPath);
        ArgumentException.ThrowIfNullOrEmpty(originalJpegPath);
        string djxl = _tools.Get(ToolId.Djxl)?.Path ?? throw new NotSupportedException("JPEG reconstruction needs djxl, which is not installed.");
        string jxl = Path.GetFullPath(jxlPath);
        string dir = _workDir ?? Path.GetDirectoryName(jxl)!;
        string reconstructed = Path.Combine(dir, $"reconstructed-{Guid.NewGuid():N}.jpg");
        string? copy = null;
        try
        {
            if (_workDir is not null)
            {
                Directory.CreateDirectory(_workDir);
                if (!string.Equals(Path.GetDirectoryName(jxl), _workDir, StringComparison.OrdinalIgnoreCase) || !Ascii.IsValid(Path.GetFileName(jxl)))
                {
                    copy = Path.Combine(_workDir, $"verify-{Guid.NewGuid():N}.jxl");
                    File.Copy(jxl, copy);
                    jxl = copy;
                }
            }

            // -J: reconstruct the original JPEG bitstream or fail (never re-encode pixels).
            var result = await _runner.RunAsync(new ProcessSpec(djxl, [Path.GetFileName(jxl), Path.GetFileName(reconstructed), "-J", "--quiet"], dir, TimeSpan.FromMinutes(10)), ct).ConfigureAwait(false);
            if (!result.Success || !File.Exists(reconstructed))
            {
                return false;
            }

            var a = new FileInfo(reconstructed);
            var b = new FileInfo(originalJpegPath);
            if (a.Length != b.Length)
            {
                return false;
            }

            byte[] hashA = await HashAsync(a.FullName, ct).ConfigureAwait(false);
            byte[] hashB = await HashAsync(b.FullName, ct).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(hashA, hashB);
        }
        finally
        {
            TryDelete(reconstructed);
            if (copy is not null)
            {
                TryDelete(copy);
            }
        }
    }

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

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
    }
}
