using System.Text.Json;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Persistence;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace); a corrupt file is
/// kept as <c>settings.json.bad-*</c> and defaults are used. <see cref="SaveDebounced"/> coalesces bursts of
/// changes (sliders, toggles) into one write; call <see cref="Flush"/> on exit.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly DataPaths _paths;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private AppSettings? _pending;

    public SettingsStore(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised after a successful write (on the writing thread).</summary>
    public event EventHandler? Saved;

    public AppSettings Load()
    {
        string file = _paths.SettingsFile;
        if (!File.Exists(file))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            return Normalize(settings);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"Settings unreadable ({e.Message}); using defaults.");
            TryQuarantine(file);
            return new AppSettings();
        }
    }

    /// <summary>Writes immediately (atomic). Cancels any pending debounced save.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Write(settings);
        }
    }

    /// <summary>Schedules a write of a snapshot of <paramref name="settings"/> after <paramref name="delay"/> (default 400 ms).</summary>
    public void SaveDebounced(AppSettings settings, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _pending = settings.Clone();
            _timer.Change(delay ?? TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes the pending debounced snapshot now, if any.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return;
            }

            var snapshot = _pending;
            _pending = null;
            Write(snapshot);
        }
    }

    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.CustomProfiles ??= [];
        s.WatchRules ??= [];
        s.ExtraToolDirs ??= [];
        s.Output ??= new Engine.OutputPlan();
        if (string.IsNullOrWhiteSpace(s.Language))
        {
            s.Language = "auto";
        }

        if (string.IsNullOrWhiteSpace(s.ActiveProfileId))
        {
            s.ActiveProfileId = "vs";
        }

        if (s.MaxConcurrency is < 1)
        {
            s.MaxConcurrency = null;
        }

        return s;
    }

    private void Write(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string file = _paths.SettingsFile;
            string temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings.", e);
        }
    }

    private static void TryQuarantine(string file)
    {
        try
        {
            File.Move(file, $"{file}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not quarantine corrupt settings: {e.Message}");
        }
    }
}
