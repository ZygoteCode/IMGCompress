using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Carbonado.Core.Logging;

namespace Carbonado.App.Startup;

/// <summary>
/// One Carbonado window per user and data folder. A second launch forwards its file arguments to the running
/// instance over a named pipe and exits immediately (no WPF initialisation on that path).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _owns;

    private SingleInstance(string key)
    {
        _pipeName = "carbonado-" + key;
        _mutex = new Mutex(initiallyOwned: false, @"Local\carbonado-" + key);
    }

    /// <summary>Raised on a background thread with the arguments sent by a secondary instance.</summary>
    public event EventHandler<string[]>? ArgumentsReceived;

    public bool IsPrimary => _owns;

    /// <summary>Creates the instance guard for a data folder (null = default profile folder).</summary>
    public static SingleInstance For(string? dataDir)
    {
        string identity = Environment.UserName + "|" + (dataDir is null ? "default" : Path.GetFullPath(dataDir).ToUpperInvariant());
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        var instance = new SingleInstance(key);
        try
        {
            instance._owns = instance._mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            instance._owns = true;
        }

        return instance;
    }

    /// <summary>Sends <paramref name="args"/> to the primary instance. Returns false if it could not be reached.</summary>
    public bool Forward(IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(args.Select(a => Path.Exists(a) ? Path.GetFullPath(a) : a).ToArray(), SingleInstanceJson.Default.StringArray);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not forward arguments to the running instance: {e.Message}");
            return false;
        }
    }

    /// <summary>Starts listening for secondary instances (primary only).</summary>
    public void Listen()
    {
        if (!_owns)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        _ = Task.Run(ListenLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_owns)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released on another thread already (process exit path).
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await server.CopyToAsync(buffer, _cts.Token).ConfigureAwait(false);
                string[]? args = JsonSerializer.Deserialize(buffer.ToArray(), SingleInstanceJson.Default.StringArray);
                ArgumentsReceived?.Invoke(this, args ?? []);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                Log.Warn($"Single-instance pipe error: {e.Message}");
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class SingleInstanceJson : System.Text.Json.Serialization.JsonSerializerContext;
