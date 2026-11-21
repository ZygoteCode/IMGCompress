using Carbonado.Core.Engine;

namespace Carbonado.Core.Persistence;

public enum IntroPreference
{
    Cinematic,
    Short,
    Off,
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>A folder auto-compressed when new images appear.</summary>
public sealed record WatchRule(string Id, string Folder, bool Recursive, string ProfileId, OutputPlan Output, bool Enabled);

/// <summary>User preferences, persisted as JSON in %APPDATA%\Carbonado\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>"auto" (follow Windows), "it" or "en".</summary>
    public string Language { get; set; } = "auto";

    public IntroPreference Intro { get; set; } = IntroPreference.Cinematic;

    /// <summary>null = follow Windows "show animations" setting.</summary>
    public bool? ReducedMotion { get; set; }

    public string ActiveProfileId { get; set; } = "vs";

    public List<CompressionProfile> CustomProfiles { get; set; } = [];

    public OutputPlan Output { get; set; } = new();

    /// <summary>null = automatic (logical cores and memory budget).</summary>
    public int? MaxConcurrency { get; set; }

    public bool PlayChime { get; set; } = true;

    public bool ShowToasts { get; set; } = true;

    public bool ExplorerIntegration { get; set; }

    public List<WatchRule> WatchRules { get; set; } = [];

    public bool LegacyScanDone { get; set; }

    public bool FirstRunDone { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Extra folders searched for encoder binaries (e.g. a legacy IMGCompress folder).</summary>
    public List<string> ExtraToolDirs { get; set; } = [];

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Language = Language,
        Intro = Intro,
        ReducedMotion = ReducedMotion,
        ActiveProfileId = ActiveProfileId,
        CustomProfiles = [.. CustomProfiles],
        Output = Output,
        MaxConcurrency = MaxConcurrency,
        PlayChime = PlayChime,
        ShowToasts = ShowToasts,
        ExplorerIntegration = ExplorerIntegration,
        WatchRules = [.. WatchRules],
        LegacyScanDone = LegacyScanDone,
        FirstRunDone = FirstRunDone,
        Window = Window,
        ExtraToolDirs = [.. ExtraToolDirs],
    };
}
