using System.Collections.ObjectModel;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Engine;
using Carbonado.Core.Migration;
using Carbonado.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Settings;

/// <summary>A localized choice for combo boxes / segmented controls.</summary>
public sealed record Option<T>(T Value, string LabelKey)
{
    public string Label => LabelKey.StartsWith("L.", StringComparison.Ordinal) ? Loc.Get(LabelKey) : LabelKey;

    public override string ToString() => Label;
}

/// <summary>A watch rule row with an editable enabled flag.</summary>
public sealed partial class WatchRuleRow(WatchRule rule, string profileName) : ObservableObject
{
    public WatchRule Rule { get; private set; } = rule;

    public string Folder => Rule.Folder;

    public string ProfileName { get; } = profileName;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = rule.Enabled;

    partial void OnEnabledChanged(bool value) => Rule = Rule with { Enabled = value };
}

/// <summary>Everything the settings page needs from the outside world (kept explicit for testability).</summary>
public sealed record SettingsContext(
    AppSettings Settings,
    Action<AppSettings> Persist,
    IDialogService Dialogs,
    IShellLauncher Shell,
    IProfileCatalog Profiles,
    DataPaths Paths,
    HistoryStore History,
    ShellIntegrationService Integration,
    string ExecutablePath,
    Action<IEnumerable<WatchRule>> ApplyWatchRules,
    Func<CancellationToken, Task<LegacyScanResult>> ScanLegacy,
    Func<LegacyScanResult, MigrationOptions, MigrationReport> ApplyMigration,
    Action<string> ApplyLanguage,
    Action<bool?> ApplyReducedMotion);

/// <summary>Settings page: appearance, output, performance, notifications, integration, watch folders, migration, data.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsContext _ctx;
    private readonly AppSettings _s;
    private bool _loading = true;

    public SettingsViewModel(SettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _s = context.Settings;

        Languages = [new("auto", "L.Settings.Language.Auto"), new("it", "Italiano"), new("en", "English")];
        Intros = [new(IntroPreference.Cinematic, "L.Settings.Intro.Cinematic"), new(IntroPreference.Short, "L.Settings.Intro.Short"), new(IntroPreference.Off, "L.Settings.Intro.Off")];
        MotionOptions = [new(null, "L.Settings.Motion.Follow"), new(true, "L.Settings.Motion.Reduced"), new(false, "L.Settings.Motion.Full")];
        Locations = [new(OutputLocation.SubfolderNextToSource, "L.Settings.Output.Subfolder"), new(OutputLocation.SameFolder, "L.Settings.Output.Same"), new(OutputLocation.CustomFolder, "L.Settings.Output.Custom")];
        Conflicts = [new(ConflictPolicy.Rename, "L.Settings.Conflict.Rename"), new(ConflictPolicy.Overwrite, "L.Settings.Conflict.Overwrite"), new(ConflictPolicy.Skip, "L.Settings.Conflict.Skip")];
        Concurrency = [new(null, "L.Settings.Concurrency.Auto"), .. Enumerable.Range(1, Environment.ProcessorCount).Select(n => new Option<int?>(n, n.ToString(Loc.Culture)))];
        Levels = [.. Enumerable.Range(LegacyLevels.Min, LegacyLevels.Max).Select(l => new Option<int>(l, LegacyLevels.ProfileFor(l).DisplayName(Loc.Language)))];

        SelectedLanguage = Languages.First(o => o.Value == _s.Language) ?? Languages[0];
        SelectedIntro = Intros.First(o => o.Value == _s.Intro);
        SelectedMotion = MotionOptions.First(o => o.Value == _s.ReducedMotion);
        SelectedLocation = Locations.First(o => o.Value == _s.Output.Location);
        SelectedConflict = Conflicts.First(o => o.Value == _s.Output.Conflict);
        SelectedConcurrency = Concurrency.FirstOrDefault(o => o.Value == _s.MaxConcurrency) ?? Concurrency[0];
        CustomFolder = _s.Output.CustomFolder ?? string.Empty;
        SubfolderName = _s.Output.SubfolderName;
        NamePattern = _s.Output.NamePattern;
        PreserveTimestamps = _s.Output.PreserveTimestamps;
        MirrorTree = _s.Output.MirrorTree;
        ReplaceOriginals = _s.Output.ReplaceOriginals;
        PlayChime = _s.PlayChime;
        ShowToasts = _s.ShowToasts;
        ExplorerIntegration = context.Integration.IsRegistered;
        SelectedLevel = Levels.First(l => l.Value == LegacyLevels.Default);
        foreach (var rule in _s.WatchRules)
        {
            AddRow(rule);
        }

        _loading = false;
    }

    public IReadOnlyList<Option<string>> Languages { get; }

    public IReadOnlyList<Option<IntroPreference>> Intros { get; }

    public IReadOnlyList<Option<bool?>> MotionOptions { get; }

    public IReadOnlyList<Option<OutputLocation>> Locations { get; }

    public IReadOnlyList<Option<ConflictPolicy>> Conflicts { get; }

    public IReadOnlyList<Option<int?>> Concurrency { get; }

    public IReadOnlyList<Option<int>> Levels { get; }

    public ObservableCollection<WatchRuleRow> WatchRules { get; } = [];

    public string DataFolder => _ctx.Paths.Root;

    [ObservableProperty]
    public partial Option<string> SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial Option<IntroPreference> SelectedIntro { get; set; }

    [ObservableProperty]
    public partial Option<bool?> SelectedMotion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFolder), nameof(IsSubfolder))]
    public partial Option<OutputLocation> SelectedLocation { get; set; }

    [ObservableProperty]
    public partial Option<ConflictPolicy> SelectedConflict { get; set; }

    [ObservableProperty]
    public partial Option<int?> SelectedConcurrency { get; set; }

    [ObservableProperty]
    public partial string CustomFolder { get; set; }

    [ObservableProperty]
    public partial string SubfolderName { get; set; }

    [ObservableProperty]
    public partial string NamePattern { get; set; }

    [ObservableProperty]
    public partial bool PreserveTimestamps { get; set; }

    [ObservableProperty]
    public partial bool MirrorTree { get; set; }

    [ObservableProperty]
    public partial bool ReplaceOriginals { get; set; }

    [ObservableProperty]
    public partial bool PlayChime { get; set; }

    [ObservableProperty]
    public partial bool ShowToasts { get; set; }

    [ObservableProperty]
    public partial bool ExplorerIntegration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan), nameof(ScanSummary))]
    public partial LegacyScanResult? LegacyScan { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial Option<int> SelectedLevel { get; set; }

    [ObservableProperty]
    public partial bool MigrateImportInputs { get; set; } = true;

    [ObservableProperty]
    public partial bool MigrateUseOutputs { get; set; } = true;

    [ObservableProperty]
    public partial bool MigrateRegisterTools { get; set; } = true;

    [ObservableProperty]
    public partial bool MigrateCreateWatch { get; set; }

    [ObservableProperty]
    public partial bool MigrateCleanOrphans { get; set; }

    [ObservableProperty]
    public partial string? MigrationReportText { get; set; }

    public bool IsCustomFolder => SelectedLocation?.Value == OutputLocation.CustomFolder;

    public bool IsSubfolder => SelectedLocation?.Value == OutputLocation.SubfolderNextToSource;

    public bool HasScan => LegacyScan is { FoundAnything: true };

    public string ScanSummary => LegacyScan is not { } scan
        ? string.Empty
        : scan.FoundAnything
            ? Loc.Format("L.Migration.Found", scan.Installs.Count, scan.Installs.Sum(i => i.InputImageCount), scan.Orphans.Count, FormatText.Bytes(scan.OrphanBytes))
            : Loc.Get("L.Migration.NothingFound");

    partial void OnSelectedLanguageChanged(Option<string> value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _s.Language = value.Value;
        _ctx.ApplyLanguage(value.Value);
        Save();
    }

    partial void OnSelectedIntroChanged(Option<IntroPreference> value)
    {
        if (!_loading && value is not null)
        {
            _s.Intro = value.Value;
            Save();
        }
    }

    partial void OnSelectedMotionChanged(Option<bool?> value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _s.ReducedMotion = value.Value;
        _ctx.ApplyReducedMotion(value.Value);
        Save();
    }

    partial void OnSelectedLocationChanged(Option<OutputLocation> value) => UpdateOutput();

    partial void OnSelectedConflictChanged(Option<ConflictPolicy> value) => UpdateOutput();

    partial void OnCustomFolderChanged(string value) => UpdateOutput();

    partial void OnSubfolderNameChanged(string value) => UpdateOutput();

    partial void OnNamePatternChanged(string value) => UpdateOutput();

    partial void OnPreserveTimestampsChanged(bool value) => UpdateOutput();

    partial void OnMirrorTreeChanged(bool value) => UpdateOutput();

    partial void OnReplaceOriginalsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            _ = ConfirmReplaceOriginalsAsync();
        }
        else
        {
            UpdateOutput();
        }
    }

    partial void OnSelectedConcurrencyChanged(Option<int?> value)
    {
        if (!_loading && value is not null)
        {
            _s.MaxConcurrency = value.Value;
            Save();
        }
    }

    partial void OnPlayChimeChanged(bool value)
    {
        if (!_loading)
        {
            _s.PlayChime = value;
            Save();
        }
    }

    partial void OnShowToastsChanged(bool value)
    {
        if (!_loading)
        {
            _s.ShowToasts = value;
            Save();
        }
    }

    partial void OnExplorerIntegrationChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            _ctx.Integration.Register(_ctx.ExecutablePath, Loc.Get("L.Shell.ContextMenu"));
        }
        else
        {
            _ctx.Integration.Unregister();
        }

        _s.ExplorerIntegration = value;
        Save();
    }

    [RelayCommand]
    private void BrowseCustomFolder()
    {
        if (_ctx.Dialogs.PickFolder(string.IsNullOrWhiteSpace(CustomFolder) ? null : CustomFolder) is { } folder)
        {
            CustomFolder = folder;
            SelectedLocation = Locations.First(o => o.Value == OutputLocation.CustomFolder);
        }
    }

    [RelayCommand]
    private void AddWatchFolder()
    {
        if (_ctx.Dialogs.PickFolder() is not { } folder || _s.WatchRules.Any(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var rule = new WatchRule(Guid.NewGuid().ToString("N")[..8], folder, Recursive: false, _ctx.Profiles.Active.Id, _s.Output, Enabled: true);
        _s.WatchRules.Add(rule);
        AddRow(rule);
        CommitWatchRules();
    }

    [RelayCommand]
    private void RemoveWatch(WatchRuleRow? row)
    {
        if (row is null)
        {
            return;
        }

        WatchRules.Remove(row);
        _s.WatchRules.RemoveAll(r => r.Id == row.Rule.Id);
        CommitWatchRules();
    }

    [RelayCommand]
    private async Task ScanLegacyAsync()
    {
        IsScanning = true;
        try
        {
            LegacyScan = await _ctx.ScanLegacy(CancellationToken.None).ConfigureAwait(true);
            MigrateCleanOrphans = LegacyScan.Orphans.Count > 0 && MigrateCleanOrphans;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ApplyMigrationAsync()
    {
        if (LegacyScan is not { FoundAnything: true } scan)
        {
            return;
        }

        if (MigrateCleanOrphans && scan.Orphans.Count > 0)
        {
            var answer = await _ctx.Dialogs.ConfirmAsync(Loc.Get("L.Migration.Orphans.Title"),
                Loc.Format("L.Migration.Orphans.Message", scan.Orphans.Count, FormatText.Bytes(scan.OrphanBytes)),
                Loc.Get("L.Migration.Orphans.Recycle"), Loc.Get("L.Common.Cancel")).ConfigureAwait(true);
            if (answer != ConfirmResult.Primary)
            {
                MigrateCleanOrphans = false;
            }
        }

        var options = new MigrationOptions(MigrateImportInputs, MigrateUseOutputs, SelectedLevel.Value, MigrateRegisterTools, MigrateCreateWatch, MigrateCleanOrphans);
        var report = _ctx.ApplyMigration(scan, options);
        MigrationReportText = Loc.Format("L.Migration.Report", report.ImportedFiles, report.Destination ?? "—",
            _ctx.Profiles.Find(report.ProfileId)?.DisplayName(Loc.Language) ?? report.ProfileId, report.ToolsRegistered,
            report.OrphansRecycled, FormatText.Bytes(report.BytesFreed));
        _loading = true;
        WatchRules.Clear();
        foreach (var rule in _s.WatchRules)
        {
            AddRow(rule);
        }

        SelectedLocation = Locations.First(o => o.Value == _s.Output.Location);
        CustomFolder = _s.Output.CustomFolder ?? string.Empty;
        _loading = false;
        CommitWatchRules();
    }

    [RelayCommand]
    private void OpenDataFolder() => _ctx.Shell.OpenFolder(_ctx.Paths.Root);

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var answer = await _ctx.Dialogs.ConfirmAsync(Loc.Get("L.Archive.Clear.Title"), Loc.Get("L.Archive.Clear.Message"),
            Loc.Get("L.Common.Delete"), Loc.Get("L.Common.Cancel"), danger: true).ConfigureAwait(true);
        if (answer == ConfirmResult.Primary)
        {
            _ctx.History.Clear();
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var answer = await _ctx.Dialogs.ConfirmAsync(Loc.Get("L.Settings.Reset.Title"), Loc.Get("L.Settings.Reset.Message"),
            Loc.Get("L.Settings.Reset.Confirm"), Loc.Get("L.Common.Cancel"), danger: true).ConfigureAwait(true);
        if (answer != ConfirmResult.Primary)
        {
            return;
        }

        var defaults = new AppSettings { FirstRunDone = true, LegacyScanDone = _s.LegacyScanDone };
        _loading = true;
        _s.Language = defaults.Language;
        _s.Intro = defaults.Intro;
        _s.ReducedMotion = defaults.ReducedMotion;
        _s.Output = defaults.Output;
        _s.MaxConcurrency = defaults.MaxConcurrency;
        _s.PlayChime = defaults.PlayChime;
        _s.ShowToasts = defaults.ShowToasts;
        SelectedLanguage = Languages[0];
        SelectedIntro = Intros[0];
        SelectedMotion = MotionOptions[0];
        SelectedLocation = Locations[0];
        SelectedConflict = Conflicts[0];
        SelectedConcurrency = Concurrency[0];
        CustomFolder = string.Empty;
        SubfolderName = defaults.Output.SubfolderName;
        NamePattern = defaults.Output.NamePattern;
        PreserveTimestamps = defaults.Output.PreserveTimestamps;
        MirrorTree = defaults.Output.MirrorTree;
        ReplaceOriginals = false;
        PlayChime = defaults.PlayChime;
        ShowToasts = defaults.ShowToasts;
        _loading = false;
        _ctx.ApplyLanguage(_s.Language);
        _ctx.ApplyReducedMotion(_s.ReducedMotion);
        Save();
    }

    private async Task ConfirmReplaceOriginalsAsync()
    {
        var answer = await _ctx.Dialogs.ConfirmAsync(Loc.Get("L.Settings.Replace.Title"), Loc.Get("L.Settings.Replace.Message"),
            Loc.Get("L.Settings.Replace.Confirm"), Loc.Get("L.Common.Cancel"), danger: true).ConfigureAwait(true);
        if (answer == ConfirmResult.Primary)
        {
            UpdateOutput();
        }
        else
        {
            _loading = true;
            ReplaceOriginals = false;
            _loading = false;
        }
    }

    private void UpdateOutput()
    {
        if (_loading || SelectedLocation is null || SelectedConflict is null)
        {
            return;
        }

        string sub = string.IsNullOrWhiteSpace(SubfolderName) ? "Carbonado" : SubfolderName.Trim();
        string pattern = string.IsNullOrWhiteSpace(NamePattern) ? "{name}" : NamePattern.Trim();
        _s.Output = new OutputPlan(SelectedLocation.Value, string.IsNullOrWhiteSpace(CustomFolder) ? null : CustomFolder.Trim(), sub, pattern,
            SelectedConflict.Value, MirrorTree, PreserveTimestamps, ReplaceOriginals);
        Save();
    }

    private void AddRow(WatchRule rule)
    {
        var row = new WatchRuleRow(rule, _ctx.Profiles.Find(rule.ProfileId)?.DisplayName(Loc.Language) ?? rule.ProfileId);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WatchRuleRow.Enabled))
            {
                int i = _s.WatchRules.FindIndex(r => r.Id == row.Rule.Id);
                if (i >= 0)
                {
                    _s.WatchRules[i] = row.Rule;
                }

                CommitWatchRules();
            }
        };
        WatchRules.Add(row);
    }

    private void CommitWatchRules()
    {
        _ctx.ApplyWatchRules(_s.WatchRules);
        Save();
    }

    private void Save() => _ctx.Persist(_s);
}
