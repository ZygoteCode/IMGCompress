using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Carbonado.Core.Tools;

/// <summary>
/// Runs external tools safely: argument lists only (no shell), no window, below-normal priority by default, stdin
/// always redirected, every child placed in a kill-on-close <see cref="JobObject"/> so no process can outlive the app,
/// timeouts and cancellation that kill the whole process tree, and bounded stderr capture.
/// </summary>
/// <remarks>
/// <see cref="Shared"/> lives for the whole process; call <see cref="KillAll"/> on shutdown (the job's kill-on-close
/// also covers crashes). Separate instances may be created and disposed, e.g. per batch or in tests.
/// </remarks>
public sealed class ProcessRunner : IDisposable
{
    /// <summary>Most characters of stderr kept per process (the head and the tail are kept, the middle elided).</summary>
    public const int MaxStderrChars = 64 * 1024;

    // Exit code given to processes terminated through the job ("KILL"), to recognise them even in a race.
    private const uint KilledExitCode = 0x4B494C4C;

    private static readonly Lazy<ProcessRunner> s_shared = new(() => new ProcessRunner(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly JobObject _job = new();
    private readonly ConcurrentDictionary<int, RunningProcess> _active = new();
    private int _disposed;

    /// <summary>The process-wide runner.</summary>
    public static ProcessRunner Shared => s_shared.Value;

    /// <summary>Child processes currently running.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>Process ids of the running children (diagnostics and tests).</summary>
    internal IReadOnlyCollection<int> ActiveProcessIds => [.. _active.Keys];

    /// <summary>Starts <paramref name="spec"/> and waits for it to exit.</summary>
    /// <returns>The result; a timeout is reported through <see cref="ProcessResult.TimedOut"/>, not an exception.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled, or <see cref="KillAll"/>/<see cref="Dispose"/> stopped the process (which is dead by then).</exception>
    /// <exception cref="InvalidOperationException">The executable could not be started.</exception>
    /// <exception cref="ObjectDisposedException">The runner was disposed.</exception>
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = CreateStartInfo(spec) };
        var clock = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{spec.FileName}': {ex.Message}", ex);
        }

        var running = new RunningProcess(process);
        if (!TryAssign(process))
        {
            Kill(process);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            throw new InvalidOperationException($"Could not place '{spec.FileName}' in the sandbox job object.");
        }

        _active[process.Id] = running;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Disposed while starting: the job handle is gone, kill directly.
                running.StoppedByRunner = true;
                Kill(process);
            }

            TrySetPriority(process, spec.Priority);
            Task stdin = WriteStdinAsync(process.StandardInput.BaseStream, spec.Stdin);
            Task<byte[]> stdout = spec.CaptureStdout ? ReadAllAsync(process.StandardOutput.BaseStream) : DrainAsync(process.StandardOutput.BaseStream);
            Task<string> stderr = ReadBoundedAsync(process.StandardError);

            bool timedOut = false;
            using var timeout = spec.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout?.Token ?? CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Kill(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                timedOut = !ct.IsCancellationRequested;
            }

            await WaitForPipesAsync(Task.WhenAll(stdout, stderr, stdin), process, linked.Token).ConfigureAwait(false);
            byte[] output = stdout.IsCompletedSuccessfully ? stdout.Result : [];
            string error = stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty;
            ct.ThrowIfCancellationRequested();
            if (running.StoppedByRunner || (!timedOut && process.ExitCode == unchecked((int)KilledExitCode)))
            {
                throw new OperationCanceledException($"'{Path.GetFileName(spec.FileName)}' was stopped by ProcessRunner.KillAll or Dispose.");
            }

            return new ProcessResult(timedOut ? -1 : process.ExitCode, output, error, clock.Elapsed, timedOut);
        }
        catch
        {
            // Whatever went wrong (cancellation included), the child must not keep running.
            Kill(process);
            throw;
        }
        finally
        {
            _active.TryRemove(process.Id, out _);
        }
    }

    /// <summary>Kills every running child (and its descendants). Their <see cref="RunAsync"/> calls end with <see cref="OperationCanceledException"/>.</summary>
    public void KillAll()
    {
        foreach (var running in _active.Values)
        {
            running.StoppedByRunner = true;
        }

        try
        {
            _job.Terminate(KilledExitCode);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently: closing the job already killed everything in it.
        }

        foreach (var running in _active.Values)
        {
            Kill(running.Process);
        }
    }

    /// <summary>Kills all children and closes the job; later <see cref="RunAsync"/> calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        KillAll();
        _job.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };
        foreach (string argument in spec.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return psi;
    }

    private bool TryAssign(Process process)
    {
        try
        {
            return _job.TryAssign(process) || process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited, or exiting (an AggregateException lists descendants that could not be terminated: the
            // job still owns them).
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Termination is asynchronous in the kernel; the job still owns the process.
        }
    }

    // The pipes close when the process (tree) has exited. A descendant that inherited them could keep them open:
    // wait for them only until the caller's token or timeout fires, then for a short grace period, then kill what is
    // left of the tree and abandon them.
    private static async Task WaitForPipesAsync(Task pipes, Process process, CancellationToken token)
    {
        if (!token.IsCancellationRequested)
        {
            try
            {
                await pipes.WaitAsync(token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Fall through to the grace period.
            }
        }

        try
        {
            await pipes.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandoned. The holder is a descendant (the process itself has exited): kill it now rather than at the
            // job's close.
            Kill(process);
        }
    }

    private static async Task WriteStdinAsync(Stream stdin, byte[]? data)
    {
        try
        {
            if (data is { Length: > 0 })
            {
                await stdin.WriteAsync(data).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The child exited (or closed stdin) before reading everything.
        }
        finally
        {
            try
            {
                stdin.Dispose();
            }
            catch (IOException)
            {
                // Broken pipe on close.
            }
        }
    }

    private static async Task<byte[]> DrainAsync(Stream stream)
    {
        await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        return [];
    }

    // Collects the stream into 1 MiB chunks, then copies once into an exact-size array.
    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        const int ChunkSize = 1 << 20;
        var chunks = new List<byte[]>();
        long total = 0;
        try
        {
            while (true)
            {
                byte[] chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
                chunks.Add(chunk);
                int filled = 0;
                while (filled < ChunkSize)
                {
                    int n = await stream.ReadAsync(chunk.AsMemory(filled, ChunkSize - filled)).ConfigureAwait(false);
                    if (n == 0)
                    {
                        total += filled;
                        return Concatenate(chunks, total, ChunkSize);
                    }

                    filled += n;
                }

                total += filled;
            }
        }
        finally
        {
            foreach (byte[] chunk in chunks)
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
    }

    private static byte[] Concatenate(List<byte[]> chunks, long total, int chunkSize)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(checked((int)total));
        long offset = 0;
        foreach (byte[] chunk in chunks)
        {
            int n = (int)Math.Min(chunkSize, total - offset);
            chunk.AsSpan(0, n).CopyTo(result.AsSpan((int)offset));
            offset += n;
        }

        return result;
    }

    // Keeps the first three quarters of the budget and a rolling tail of the last quarter.
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int HeadChars = MaxStderrChars / 4 * 3;
        const int TailChars = MaxStderrChars - HeadChars;
        var head = new StringBuilder();
        var tail = new char[TailChars];
        int tailStart = 0, tailCount = 0;
        bool elided = false;
        char[] buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int i = 0;
            if (head.Length < HeadChars)
            {
                int take = Math.Min(n, HeadChars - head.Length);
                head.Append(buffer, 0, take);
                i = take;
            }

            for (; i < n; i++)
            {
                if (tailCount == TailChars)
                {
                    elided = true;
                    tail[tailStart] = buffer[i];
                    tailStart = (tailStart + 1) % TailChars;
                }
                else
                {
                    tail[(tailStart + tailCount) % TailChars] = buffer[i];
                    tailCount++;
                }
            }
        }

        if (tailCount > 0)
        {
            if (elided)
            {
                head.Append("\n…\n");
            }

            for (int i = 0; i < tailCount; i++)
            {
                head.Append(tail[(tailStart + i) % TailChars]);
            }
        }

        return head.ToString();
    }

    private sealed class RunningProcess(Process process)
    {
        public Process Process { get; } = process;

        public volatile bool StoppedByRunner;
    }
}
