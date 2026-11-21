namespace Carbonado.App.Startup;

/// <summary>Intro override from the command line.</summary>
public enum IntroOverride
{
    None,
    Cinematic,
    Short,
    Off,
}

/// <summary>Parsed command line of Carbonado.exe.</summary>
/// <param name="Files">Files or folders to queue at startup (or forward to the running instance).</param>
/// <param name="DataDir">Isolated data folder (tests, portable use).</param>
/// <param name="Profile">Profile id to activate.</param>
/// <param name="Start">Start compressing the queued files immediately.</param>
/// <param name="SnapshotDir">Render every page to PNG into this folder and exit (visual regression).</param>
/// <param name="NewInstance">Do not forward to an already running instance.</param>
public sealed record CommandLineOptions(
    IReadOnlyList<string> Files,
    string? DataDir,
    IntroOverride Intro,
    string? Profile,
    bool Start,
    string? SnapshotDir,
    bool NewInstance,
    string? Language)
{
    public static CommandLineOptions Empty { get; } = new([], null, IntroOverride.None, null, false, null, false, null);
}

public static class CommandLine
{
    /// <summary>Parses arguments. Unknown switches are ignored (never fatal for a GUI app); bare values are files.</summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        string? dataDir = null, profile = null, snapshot = null, language = null;
        var intro = IntroOverride.None;
        bool start = false, newInstance = false;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--data-dir":
                    dataDir = Next();
                    break;
                case "--intro":
                    intro = (Next() ?? string.Empty).ToLowerInvariant() switch
                    {
                        "cinematic" => IntroOverride.Cinematic,
                        "short" => IntroOverride.Short,
                        "off" or "none" => IntroOverride.Off,
                        _ => IntroOverride.None,
                    };
                    break;
                case "--profile":
                    profile = Next();
                    break;
                case "--start":
                    start = true;
                    break;
                case "--snapshot":
                    snapshot = Next();
                    break;
                case "--new-instance":
                    newInstance = true;
                    break;
                case "--lang":
                    language = Next();
                    break;
                default:
                    if (!a.StartsWith("--", StringComparison.Ordinal))
                    {
                        files.Add(a);
                    }

                    break;
            }
        }

        return new CommandLineOptions(files, dataDir, intro, profile, start, snapshot, newInstance, language);
    }
}
