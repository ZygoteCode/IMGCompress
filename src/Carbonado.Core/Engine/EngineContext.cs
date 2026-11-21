using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Engine;

/// <summary>
/// Everything the engine shares between jobs: the located tools, a private <see cref="ProcessRunner"/> (its Job Object
/// owns every encoder process), the loader, the installed encoders, the verifier and a private session temp folder.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> kills every tool still running (their jobs end as cancelled or failed) and deletes the session
/// folder. The context is thread-safe; one instance serves any number of concurrent jobs.
/// </remarks>
public sealed class EngineContext : IDisposable
{
    private int _disposed;

    private EngineContext(ToolLocator tools, EngineOptions options, string sessionTemp)
    {
        Tools = tools;
        Options = options;
        SessionTemp = sessionTemp;
        Runner = new ProcessRunner();
        Loader = new ImageLoader(tools, Runner, Path.Combine(sessionTemp, "decode"));
        Encoders = new EncoderRegistry(tools, Runner);
        Verifier = new OutputVerifier(Loader, tools, Runner, Path.Combine(sessionTemp, "verify"));
    }

    /// <summary>The located encoder and decoder tools.</summary>
    public ToolLocator Tools { get; }

    /// <summary>
    /// Runs every tool of this context, sandboxed in its own Job Object (not <see cref="ProcessRunner.Shared"/>):
    /// <see cref="Dispose"/> kills them all.
    /// </summary>
    public ProcessRunner Runner { get; }

    /// <summary>Decodes sources and outputs (temporary decode files live in the session folder).</summary>
    public IImageLoader Loader { get; }

    /// <summary>The encoders whose tools are installed.</summary>
    public EncoderRegistry Encoders { get; }

    /// <summary>Proves JPEG → JPEG XL transcodes reversible.</summary>
    public OutputVerifier Verifier { get; }

    /// <summary>The private session folder: every job works in its own subfolder, deleted when the job ends.</summary>
    public string SessionTemp { get; }

    /// <summary>The options the context was created with (also used by <see cref="BatchScheduler"/>).</summary>
    public EngineOptions Options { get; }

    /// <summary>
    /// Creates the context: a session folder under <see cref="EngineOptions.TempRoot"/> (or the user's data temp root)
    /// and the encoder registry for <paramref name="tools"/>.
    /// </summary>
    /// <exception cref="IOException">The session folder cannot be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The temp root is not writable.</exception>
    public static EngineContext Create(ToolLocator tools, EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);
        string root = string.IsNullOrWhiteSpace(options.TempRoot)
            ? DataPaths.Resolve(null, AppContext.BaseDirectory).TempRoot
            : Path.GetFullPath(options.TempRoot);
        Directory.CreateDirectory(root);
        string session = Persistence.SessionTemp.Create(root);
        try
        {
            return new EngineContext(tools, options, session);
        }
        catch
        {
            Persistence.SessionTemp.Delete(session);
            throw;
        }
    }

    /// <summary>A new, empty work folder for one job inside <see cref="SessionTemp"/>.</summary>
    internal string CreateJobFolder(Guid jobId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string dir = Path.Combine(SessionTemp, "job-" + jobId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Kills every running tool and deletes the session folder (best effort).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Runner.Dispose();
        Persistence.SessionTemp.Delete(SessionTemp);
    }
}
