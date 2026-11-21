using System.Collections.ObjectModel;
using Carbonado.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Shell;

/// <summary>A command reachable from the palette (Ctrl+K).</summary>
/// <param name="TitleKey">Localization key of the title (or a literal title when <paramref name="Literal"/> is true).</param>
public sealed record PaletteCommand(string TitleKey, string IconKey, string? Shortcut, Action Execute, Func<bool>? CanExecute = null, bool Literal = false)
{
    public string Title => Literal ? TitleKey : Loc.Get(TitleKey);
}

/// <summary>Fuzzy-filtered command list with keyboard selection.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly Func<IEnumerable<PaletteCommand>> _source;

    public CommandPaletteViewModel(Func<IEnumerable<PaletteCommand>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Results fill on Refresh, which the shell calls when the palette opens: building the list touches the
        // clipboard and every string table entry, which has no business on the startup path.
        _source = source;
    }

    /// <summary>Raised after a command ran (the shell closes the palette).</summary>
    public event EventHandler? Executed;

    public ObservableCollection<PaletteCommand> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>Score of <paramref name="text"/> for <paramref name="query"/>: subsequence match, bonus for word starts and contiguity. -1 = no match.</summary>
    public static int Score(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        string t = text.ToLowerInvariant();
        string q = query.Trim().ToLowerInvariant();
        int score = 0, ti = 0, streak = 0;
        foreach (char c in q)
        {
            int found = t.IndexOf(c, ti);
            if (found < 0)
            {
                return -1;
            }

            bool wordStart = found == 0 || !char.IsLetterOrDigit(t[found - 1]);
            streak = found == ti ? streak + 1 : 0;
            score += 1 + (wordStart ? 6 : 0) + (streak * 3) - Math.Min(found - ti, 5);
            ti = found + 1;
        }

        return score + (t.StartsWith(q, StringComparison.Ordinal) ? 20 : 0);
    }

    public void Refresh()
    {
        var ranked = _source()
            .Where(c => c.CanExecute?.Invoke() ?? true)
            .Select(c => (Command: c, Score: Score(c.Title, Query)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Command.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Command)
            .ToList();
        Results.Clear();
        foreach (var c in ranked)
        {
            Results.Add(c);
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }

    [RelayCommand]
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        SelectedIndex = ((SelectedIndex + delta) % Results.Count + Results.Count) % Results.Count;
    }

    [RelayCommand]
    public void ExecuteSelected()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Results.Count)
        {
            Run(Results[SelectedIndex]);
        }
    }

    [RelayCommand]
    public void Run(PaletteCommand? command)
    {
        if (command is null)
        {
            return;
        }

        Query = string.Empty;
        Executed?.Invoke(this, EventArgs.Empty);
        command.Execute();
    }

    partial void OnQueryChanged(string value) => Refresh();
}
