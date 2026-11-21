namespace Carbonado.Core.Persistence;

/// <summary>
/// Where Carbonado keeps its data. Default: settings in %APPDATA%\Carbonado, history/temp/logs in
/// %LOCALAPPDATA%\Carbonado. Portable mode (a <c>portable.flag</c> file next to the executable) or an explicit
/// override directory (<c>--data-dir</c>) keeps everything under one folder.
/// </summary>
public sealed class DataPaths
{
    public const string PortableFlagFile = "portable.flag";

    private DataPaths(string root, string localRoot, bool isPortable)
    {
        Root = root;
        LocalRoot = localRoot;
        IsPortable = isPortable;
    }

    /// <summary>Roaming data folder (settings).</summary>
    public string Root { get; }

    /// <summary>Machine-local data folder (history, temp, logs).</summary>
    public string LocalRoot { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string HistoryFile => Path.Combine(LocalRoot, "history.jsonl");

    public string StatsFile => Path.Combine(LocalRoot, "stats.json");

    public string TempRoot => Path.Combine(LocalRoot, "temp");

    public string LogsDir => Path.Combine(LocalRoot, "logs");

    /// <summary>
    /// Resolves the data folders. <paramref name="overrideDir"/> wins, then portable mode, then the user profile.
    /// </summary>
    public static DataPaths Resolve(string? overrideDir, string appBaseDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            string full = Path.GetFullPath(overrideDir);
            return new DataPaths(full, full, isPortable: false);
        }

        if (File.Exists(Path.Combine(appBaseDir, PortableFlagFile)))
        {
            string data = Path.Combine(appBaseDir, "data");
            return new DataPaths(data, data, isPortable: true);
        }

        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Carbonado");
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Carbonado");
        return new DataPaths(roaming, local, isPortable: false);
    }

    /// <summary>Creates every folder (idempotent).</summary>
    public DataPaths EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(LogsDir);
        return this;
    }
}
