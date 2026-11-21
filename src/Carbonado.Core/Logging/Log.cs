using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Carbonado.Core.Logging;

/// <summary>
/// Tiny asynchronous file logger: one file per day (logs\carbonado-yyyyMMdd.log), 14 days retention.
/// Calls never block on disk; errors flush promptly. Before <see cref="Init"/> messages go to Debug output only.
/// </summary>
public static class Log
{
    private static Channel<string>? s_channel;
    private static Task? s_pump;
    private static string? s_dir;

    public static bool IsInitialized => s_channel is not null;

    public static void Init(string logsDir)
    {
        if (s_channel is not null)
        {
            return;
        }

        Directory.CreateDirectory(logsDir);
        s_dir = logsDir;
        s_channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        s_pump = Task.Run(PumpAsync);
        _ = Task.Run(() => Prune(logsDir, TimeSpan.FromDays(14)));
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message} {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");

    /// <summary>Waits (bounded) until queued messages are written. Call on shutdown.</summary>
    public static void Flush(TimeSpan? timeout = null)
    {
        var channel = s_channel;
        var pump = s_pump;
        if (channel is null || pump is null)
        {
            return;
        }

        channel.Writer.TryComplete();
        pump.Wait(timeout ?? TimeSpan.FromMilliseconds(250));
        s_channel = null;
        s_pump = null;
    }

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,3}] {message}");
        System.Diagnostics.Debug.WriteLine(line);
        s_channel?.Writer.TryWrite(line);
    }

    private static async Task PumpAsync()
    {
        var reader = s_channel!.Reader;
        var sb = new StringBuilder();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            sb.Clear();
            while (reader.TryRead(out string? line))
            {
                sb.AppendLine(line);
            }

            try
            {
                string file = Path.Combine(s_dir!, $"carbonado-{DateTime.Now:yyyyMMdd}.log");
                await File.AppendAllTextAsync(file, sb.ToString()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Prune(string dir, TimeSpan keep)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("carbonado-*.log"))
            {
                if (DateTime.UtcNow - f.LastWriteTimeUtc > keep)
                {
                    f.Delete();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
