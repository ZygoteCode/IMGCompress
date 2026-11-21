using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Forge;

/// <summary>Output format override chosen in the Forge toolbar (null format = profile decides).</summary>
public sealed record FormatChoice(OutputFormat? Format, string Label);

/// <summary>Effort override (null = the profile's effort).</summary>
public sealed record EffortChoice(EffortLevel? Value, string LabelKey)
{
    public string Label => Loc.Get(LabelKey);
}

/// <summary>A clarity-grade chip bound two-way to the selected profile.</summary>
public sealed class ProfileChoice(ForgeViewModel owner, CompressionProfile profile) : ObservableObject
{
    public CompressionProfile Profile { get; } = profile;

    public string Grade => Profile.Grade;

    public string Label => Profile.DisplayName(Loc.Language);

    public string? Description => Profile.DisplayDescription(Loc.Language);

    public bool IsActive
    {
        get => owner.SelectedProfile?.Id == Profile.Id;
        set
        {
            if (value && owner.SelectedProfile?.Id != Profile.Id)
            {
                owner.SelectedProfile = Profile;
            }
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>The main page: queue, profile/format/effort choice, run control and live totals.</summary>
public sealed partial class ForgeViewModel : ObservableObject, IDisposable
{
    private readonly IBatchProcessor _batch;
    private readonly IProfileCatalog _profiles;
    private readonly Func<OutputPlan> _outputPlan;
    private readonly IThumbnailService _thumbnails;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly IToastService _toasts;
    private readonly ISoundService _sound;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, QueueItemViewModel> _byJob = [];
    private readonly ConcurrentDictionary<Guid, JobProgress> _latestProgress = new();
    private readonly SemaphoreSlim _thumbGate = new(4);
    private readonly CancellationTokenSource _life = new();
    private int _flushScheduled;

    public ForgeViewModel(
        IBatchProcessor batch,
        IProfileCatalog profiles,
        Func<OutputPlan> outputPlan,
        IThumbnailService thumbnails,
        IUiDispatcher ui,
        IDialogService dialogs,
        IShellLauncher shell,
        IToastService toasts,
        ISoundService sound)
    {
        _batch = batch;
        _profiles = profiles;
        _outputPlan = outputPlan;
        _thumbnails = thumbnails;
        _ui = ui;
        _dialogs = dialogs;
        _shell = shell;
        _toasts = toasts;
        _sound = sound;

        SelectedProfile = profiles.Active;
        FormatChoices =
        [
            new FormatChoice(null, "Auto"),
            new FormatChoice(OutputFormat.Jxl, "JXL"),
            new FormatChoice(OutputFormat.Avif, "AVIF"),
            new FormatChoice(OutputFormat.WebP, "WebP"),
            new FormatChoice(OutputFormat.Jpeg, "JPEG"),
            new FormatChoice(OutputFormat.Png, "PNG"),
        ];
        SelectedFormat = FormatChoices[0];
        EffortChoices =
        [
            new EffortChoice(null, "L.Effort.Profile"),
            new EffortChoice(EffortLevel.Fast, "L.Effort.Fast"),
            new EffortChoice(EffortLevel.Balanced, "L.Effort.Balanced"),
            new EffortChoice(EffortLevel.Max, "L.Effort.Max"),
            new EffortChoice(EffortLevel.Insane, "L.Effort.Insane"),
        ];
        SelectedEffort = EffortChoices[0];
        GradeChoices = [.. new[] { "flawless", "vvs", "vs", "si", "i" }.Select(id => profiles.Find(id)).OfType<CompressionProfile>().Select(p => new ProfileChoice(this, p))];

        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            RecomputeTotals();
        };
        _batch.Progress += OnBatchProgress;
        _batch.JobCompleted += OnJobCompleted;
        _batch.BatchCompleted += OnBatchCompleted;
        _batch.StateChanged += OnBatchStateChanged;
        _profiles.Changed += OnProfilesChanged;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Raised when the user asks to compare an item (the shell navigates to Compare).</summary>
    public event EventHandler<QueueItemViewModel>? CompareRequested;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    public IReadOnlyList<CompressionProfile> Profiles => _profiles.All;

    public IReadOnlyList<FormatChoice> FormatChoices { get; }

    public IReadOnlyList<EffortLevel> Efforts { get; } = [EffortLevel.Fast, EffortLevel.Balanced, EffortLevel.Max, EffortLevel.Insane];

    public IReadOnlyList<EffortChoice> EffortChoices { get; }

    /// <summary>The five clarity grades (FL, VVS, VS, SI, I) shown as chips.</summary>
    public IReadOnlyList<ProfileChoice> GradeChoices { get; }

    [ObservableProperty]
    public partial EffortChoice SelectedEffort { get; set; }

    /// <summary>Localized description of where outputs go.</summary>
    public string DestinationText => _outputPlan() switch
    {
        { ReplaceOriginals: true } => Loc.Get("L.Settings.Output.Replace"),
        { Location: OutputLocation.SameFolder } => Loc.Get("L.Forge.Destination.Same"),
        { Location: OutputLocation.CustomFolder, CustomFolder: { } folder } => folder,
        var plan => Loc.Format("L.Forge.Destination.Subfolder", plan.SubfolderName),
    };

    /// <summary>"3 of 12 images".</summary>
    public string ProgressText => Loc.Format("L.Forge.Summary.Files", CompletedCount + KeptCount, Items.Count);

    public bool HasItems => Items.Count > 0;

    /// <summary>Paste from the clipboard (wired by the shell, which owns clipboard access).</summary>
    public System.Windows.Input.ICommand? PasteCommand { get; set; }

    [ObservableProperty]
    public partial CompressionProfile SelectedProfile { get; set; }

    [ObservableProperty]
    public partial FormatChoice SelectedFormat { get; set; }

    /// <summary>Null = use the profile's effort.</summary>
    [ObservableProperty]
    public partial EffortLevel? EffortOverride { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand))]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial long TotalOriginalBytes { get; set; }

    [ObservableProperty]
    public partial long TotalOutputBytes { get; set; }

    [ObservableProperty]
    public partial double SavedFraction { get; set; }

    [ObservableProperty]
    public partial int CompletedCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    [ObservableProperty]
    public partial int KeptCount { get; set; }

    [ObservableProperty]
    public partial int PendingCount { get; set; }

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    [ObservableProperty]
    public partial string? ActivityText { get; set; }

    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    public long SavedBytes => Math.Max(0, TotalOriginalBytes - TotalOutputBytes);

    /// <summary>Adds files and folders (folders recursively). Enumeration runs off the UI thread.</summary>
    public async Task<int> AddPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var list = paths.ToList();
        IsAdding = true;
        try
        {
            var found = await Task.Run(() => Expand(list)).ConfigureAwait(true);
            int added = 0;
            foreach (var (path, root, bytes) in found)
            {
                if (!_known.Add(path))
                {
                    continue;
                }

                var item = new QueueItemViewModel(path, root, bytes);
                Items.Add(item);
                added++;
                _ = LoadThumbnailAsync(item);
            }

            StartCommand.NotifyCanExecuteChanged();
            RecomputeTotals();
            return added;
        }
        finally
        {
            IsAdding = false;
        }
    }

    /// <summary>The profile actually used for new jobs: selected profile plus toolbar overrides.</summary>
    public CompressionProfile EffectiveProfile()
    {
        var p = SelectedProfile;
        if (SelectedFormat.Format is { } fmt)
        {
            p = p with { Strategy = FormatStrategy.Fixed, Formats = Fallbacks(fmt) };
        }

        if (EffortOverride is { } effort)
        {
            p = p with { Effort = effort };
        }

        return p;
    }

    public void Dispose()
    {
        _life.Cancel();
        _batch.Progress -= OnBatchProgress;
        _batch.JobCompleted -= OnJobCompleted;
        _batch.BatchCompleted -= OnBatchCompleted;
        _batch.StateChanged -= OnBatchStateChanged;
        _profiles.Changed -= OnProfilesChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        _thumbGate.Dispose();
        _life.Dispose();
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = _dialogs.PickFiles();
        if (files.Count > 0)
        {
            await AddPathsAsync(files).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_dialogs.PickFolder() is { } folder)
        {
            await AddPathsAsync([folder]).ConfigureAwait(true);
        }
    }

    private bool CanStart() => !IsRunning && Items.Any(i => i.Status == QueueItemStatus.Pending);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var profile = EffectiveProfile();
        var plan = _outputPlan();
        int queued = 0;
        foreach (var item in Items.Where(i => i.Status == QueueItemStatus.Pending))
        {
            var job = CompressionJob.Create(item.Path, profile, plan, item.Root);
            item.JobId = job.Id;
            _byJob[job.Id] = item;
            item.Status = QueueItemStatus.Working;
            item.StageText = Loc.Get("L.Stage.Queued");
            _batch.Enqueue(job);
            queued++;
        }

        if (queued == 0)
        {
            return;
        }

        IsRunning = true;
        IsPaused = false;
        ActivityText = Loc.Format("L.Forge.Activity.Started", queued);
        _batch.Start();
        RecomputeTotals();
    }

    private bool CanPause() => IsRunning && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _batch.Pause();
        IsPaused = true;
        ActivityText = Loc.Get("L.Forge.Activity.Paused");
    }

    private bool CanResume() => IsRunning && IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _batch.Resume();
        IsPaused = false;
        ActivityText = Loc.Get("L.Forge.Activity.Resumed");
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _batch.CancelAll();
        ActivityText = Loc.Get("L.Forge.Activity.Cancelling");
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (IsRunning)
        {
            var answer = await _dialogs.ConfirmAsync(
                Loc.Get("L.Forge.ClearRunning.Title"), Loc.Get("L.Forge.ClearRunning.Message"),
                Loc.Get("L.Common.CancelAndClear"), Loc.Get("L.Common.Keep"), danger: true).ConfigureAwait(true);
            if (answer != ConfirmResult.Primary)
            {
                return;
            }

            _batch.CancelAll();
        }

        Items.Clear();
        _known.Clear();
        _byJob.Clear();
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(i => i.IsFinished).ToList())
        {
            Items.Remove(item);
            _known.Remove(item.Path);
            if (item.JobId is { } id)
            {
                _byJob.Remove(id);
            }
        }
    }

    [RelayCommand]
    private void Remove(QueueItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsWorking && item.JobId is { } id)
        {
            _batch.Cancel(id);
        }

        Items.Remove(item);
        _known.Remove(item.Path);
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Retry(QueueItemViewModel? item)
    {
        if (item is null || item.IsWorking)
        {
            return;
        }

        item.Reset();
        StartCommand.NotifyCanExecuteChanged();
        RecomputeTotals();
    }

    [RelayCommand]
    private void OpenOutput(QueueItemViewModel? item)
    {
        if (item?.OutputPath is { } output && File.Exists(output))
        {
            _shell.OpenFolder(Path.GetDirectoryName(output)!, output);
        }
        else if (item is not null)
        {
            _shell.OpenFolder(item.Folder, item.Path);
        }
    }

    [RelayCommand]
    private void Compare(QueueItemViewModel? item)
    {
        if (item?.Result is not null)
        {
            CompareRequested?.Invoke(this, item);
        }
    }

    partial void OnSelectedProfileChanged(CompressionProfile value)
    {
        if (value is not null && _profiles.Active.Id != value.Id)
        {
            _profiles.SetActive(value.Id);
        }

        foreach (var choice in GradeChoices ?? [])
        {
            choice.Refresh();
        }
    }

    partial void OnSelectedEffortChanged(EffortChoice value) => EffortOverride = value?.Value;

    /// <summary>Refreshes the destination text after the output settings changed.</summary>
    public void RefreshDestination() => OnPropertyChanged(nameof(DestinationText));

    private static IReadOnlyList<OutputFormat> Fallbacks(OutputFormat fmt) => fmt switch
    {
        // JPEG cannot carry alpha: transparent sources fall back to WebP lossless/lossy.
        OutputFormat.Jpeg => [OutputFormat.Jpeg, OutputFormat.WebP],
        _ => [fmt],
    };

    private static List<(string Path, string? Root, long Bytes)> Expand(List<string> paths)
    {
        var result = new List<(string, string?, long)>();
        foreach (string raw in paths)
        {
            string p;
            try
            {
                p = Path.GetFullPath(raw.Trim('"'));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string f in files.Where(FormatInfo.IsInputExtension).Where(f => !IsInsideOutputFolder(f, p)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add((f, p, SafeLength(f)));
                }
            }
            else if (File.Exists(p) && FormatInfo.IsInputExtension(p))
            {
                result.Add((p, null, SafeLength(p)));
            }
        }

        return result;
    }

    /// <summary>Skip previous Carbonado outputs ("…\Carbonado\…") when adding a folder recursively.</summary>
    private static bool IsInsideOutputFolder(string file, string root)
    {
        string relative = Path.GetRelativePath(root, file);
        return relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(s => s.Equals("Carbonado", StringComparison.OrdinalIgnoreCase));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task LoadThumbnailAsync(QueueItemViewModel item)
    {
        try
        {
            await _thumbGate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                var info = await _thumbnails.LoadAsync(item.Path, 96, _life.Token).ConfigureAwait(false);
                if (info is not null)
                {
                    _ui.Post(() =>
                    {
                        item.Thumbnail = info.Image;
                        if (info.Width > 0 && item.Width == 0)
                        {
                            item.Width = info.Width;
                            item.Height = info.Height;
                        }
                    });
                }
            }
            finally
            {
                _thumbGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnBatchProgress(object? sender, JobProgress progress)
    {
        _latestProgress[progress.JobId] = progress;
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ui.Post(FlushProgress);
        }
    }

    private void FlushProgress()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        foreach (var id in _latestProgress.Keys)
        {
            if (_latestProgress.TryRemove(id, out var p) && _byJob.TryGetValue(id, out var item))
            {
                item.Apply(p);
                if (p.Stage == JobStage.Encoding && p.CandidateId is not null)
                {
                    ActivityText = $"{item.FileName} · {item.StageText}";
                }
            }
        }

        RecomputeProgress();
    }

    private void OnJobCompleted(object? sender, CompressionResult result) => _ui.Post(() =>
    {
        if (_byJob.TryGetValue(result.JobId, out var item))
        {
            _latestProgress.TryRemove(result.JobId, out _);
            item.Apply(result);
            if (result.Outcome == JobOutcome.Compressed)
            {
                ActivityText = Loc.Format("L.Forge.Activity.Done", item.FileName, FormatText.Percent(result.SavedFraction));
            }
        }

        RecomputeTotals();
    });

    private void OnBatchCompleted(object? sender, BatchSummary summary) => _ui.Post(() =>
    {
        IsRunning = false;
        IsPaused = false;
        RecomputeTotals();
        if (summary.Total == 0)
        {
            return;
        }

        ActivityText = Loc.Format("L.Forge.Activity.Finished", summary.Compressed + summary.Kept, summary.Total, FormatText.Bytes(summary.SavedBytes));
        var kind = summary.Failed > 0 ? ToastKind.Warning : ToastKind.Success;
        _toasts.Show(
            Loc.Get(summary.Failed > 0 ? "L.Toast.FinishedWithErrors.Title" : "L.Toast.Finished.Title"),
            Loc.Format("L.Toast.Finished.Message", summary.Compressed + summary.Kept, FormatText.Bytes(summary.SavedBytes), FormatText.Percent(summary.SavedFraction), summary.Failed),
            kind);
        _sound.PlayCompletion();
    });

    private void OnBatchStateChanged(object? sender, BatchState state) => _ui.Post(() =>
    {
        IsPaused = state == BatchState.Paused;
        if (state == BatchState.Idle && IsRunning && !Items.Any(i => i.IsWorking))
        {
            IsRunning = false;
        }
    });

    private void OnProfilesChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        OnPropertyChanged(nameof(Profiles));
        var active = _profiles.Active;
        if (SelectedProfile?.Id != active.Id || !ReferenceEquals(SelectedProfile, active))
        {
            SelectedProfile = active;
        }
    });

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
        {
            item.RefreshTexts();
        }

        foreach (var choice in GradeChoices)
        {
            choice.Refresh();
        }

        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(EffortChoices));
    }

    private void RecomputeProgress()
    {
        var run = Items.Where(i => i.JobId is not null).ToList();
        OverallProgress = run.Count == 0 ? 0 : run.Sum(i => i.IsFinished ? 1 : i.Progress) / run.Count;
    }

    private void RecomputeTotals()
    {
        long original = 0, output = 0;
        int done = 0, failed = 0, kept = 0, pending = 0;
        foreach (var item in Items)
        {
            switch (item.Status)
            {
                case QueueItemStatus.Done:
                    done++;
                    original += item.OriginalBytes;
                    output += item.OutputBytes;
                    break;
                case QueueItemStatus.Kept:
                    kept++;
                    original += item.OriginalBytes;
                    output += item.OriginalBytes;
                    break;
                case QueueItemStatus.Failed:
                    failed++;
                    break;
                case QueueItemStatus.Pending:
                    pending++;
                    break;
            }
        }

        TotalOriginalBytes = original;
        TotalOutputBytes = output;
        SavedFraction = original > 0 ? Math.Max(0, 1.0 - ((double)output / original)) : 0;
        CompletedCount = done;
        FailedCount = failed;
        KeptCount = kept;
        PendingCount = pending;
        OnPropertyChanged(nameof(SavedBytes));
        OnPropertyChanged(nameof(ProgressText));
        RecomputeProgress();
        StartCommand.NotifyCanExecuteChanged();
    }
}
