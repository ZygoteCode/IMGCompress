using System.Collections.ObjectModel;
using System.Globalization;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Archive;

/// <summary>A history line prepared for display.</summary>
public sealed class HistoryRow(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;

    public string FileName => Path.GetFileName(Entry.Input);

    public string Folder => Path.GetDirectoryName(Entry.Input) ?? string.Empty;

    public string When => Entry.At.ToLocalTime().ToString("g", Loc.Culture);

    public string FormatText => Entry.Format is { } f ? FormatInfo.DisplayName(f) : string.Empty;

    public bool IsCompressed => Entry.Outcome == JobOutcome.Compressed;

    public bool IsFailed => Entry.Outcome == JobOutcome.Failed;

    public double SavedFraction => IsCompressed && Entry.OriginalBytes > 0 ? Math.Max(0, 1.0 - ((double)Entry.OutputBytes / Entry.OriginalBytes)) : 0;

    public string SavedText => Entry.Outcome switch
    {
        JobOutcome.Compressed => "−" + Services.FormatText.Percent(SavedFraction),
        JobOutcome.KeptOriginal => Loc.Get("L.Status.KeptShort"),
        JobOutcome.Failed => Loc.Get("L.Status.Failed"),
        _ => string.Empty,
    };

    public string SizesText => IsCompressed
        ? $"{Services.FormatText.Bytes(Entry.OriginalBytes)} → {Services.FormatText.Bytes(Entry.OutputBytes)}"
        : Services.FormatText.Bytes(Entry.OriginalBytes);

    public string ScoreText => Entry.Score is { } s && IsCompressed ? Services.FormatText.Score(s) : string.Empty;
}

/// <summary>A slice of the format donut.</summary>
public sealed record FormatShare(OutputFormat Format, int Count, double Fraction)
{
    public string Label => FormatInfo.DisplayName(Format);
}

/// <summary>Archive page: lifetime stats, last 14 days of savings, format distribution and recent jobs.</summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    public const int Days = 14;
    private readonly HistoryStore _history;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private int _refreshQueued;

    public ArchiveViewModel(HistoryStore history, IUiDispatcher ui, IDialogService dialogs, IShellLauncher shell)
    {
        _history = history;
        _ui = ui;
        _dialogs = dialogs;
        _shell = shell;
        _history.Appended += (_, _) =>
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                _ = Task.Delay(500).ContinueWith(_ => _ui.Post(() => _ = RefreshAsync()), TaskScheduler.Default);
            }
        };
        Loc.LanguageChanged += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<HistoryRow> Recent { get; } = [];

    [ObservableProperty]
    public partial LifetimeStats Stats { get; set; } = LifetimeStats.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<double> DailySaved { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> DailyLabels { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FormatShare> FormatShares { get; set; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public string FilesText => Stats.Files.ToString("N0", Loc.Culture);

    public string SavedText => FormatText.Bytes(Stats.SavedBytes);

    public string RatioText => FormatText.Percent(Stats.SavedFraction);

    /// <summary>Daily saved bytes for the last <see cref="Days"/> days (oldest first) plus labels.</summary>
    public static (double[] Values, string[] Labels) Daily(IEnumerable<HistoryEntry> entries, DateTime today, CultureInfo culture)
    {
        var values = new double[Days];
        var labels = new string[Days];
        DateTime start = today.Date.AddDays(-(Days - 1));
        for (int i = 0; i < Days; i++)
        {
            labels[i] = start.AddDays(i).ToString("dd", culture);
        }

        foreach (var e in entries.Where(e => e.Outcome == JobOutcome.Compressed))
        {
            int index = (int)(e.At.LocalDateTime.Date - start).TotalDays;
            if (index >= 0 && index < Days)
            {
                values[index] += Math.Max(0, e.OriginalBytes - e.OutputBytes);
            }
        }

        return (values, labels);
    }

    public async Task RefreshAsync()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        var (recent, stats) = await Task.Run(() => (_history.ReadRecent(2000), _history.Stats())).ConfigureAwait(true);
        Stats = stats;
        var (values, labels) = Daily(recent, DateTime.Now, Loc.Culture);
        DailySaved = values;
        DailyLabels = labels;
        int total = stats.ByFormat.Values.Sum();
        FormatShares = [.. stats.ByFormat.OrderByDescending(kv => kv.Value).Select(kv => new FormatShare(kv.Key, kv.Value, total > 0 ? (double)kv.Value / total : 0))];
        Recent.Clear();
        foreach (var e in recent.Take(200))
        {
            Recent.Add(new HistoryRow(e));
        }

        IsEmpty = stats.Files == 0 && Recent.Count == 0;
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(SavedText));
        OnPropertyChanged(nameof(RatioText));
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        var answer = await _dialogs.ConfirmAsync(Loc.Get("L.Archive.Clear.Title"), Loc.Get("L.Archive.Clear.Message"),
            Loc.Get("L.Common.Delete"), Loc.Get("L.Common.Cancel"), danger: true).ConfigureAwait(true);
        if (answer == ConfirmResult.Primary)
        {
            _history.Clear();
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Open(HistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        string target = row.Entry.Output is { } o && File.Exists(o) ? o : row.Entry.Input;
        _shell.OpenFolder(Path.GetDirectoryName(target) ?? string.Empty, target);
    }
}
