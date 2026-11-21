using Carbonado.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Shell;

public enum PageKind
{
    Forge,
    Compare,
    Profiles,
    Archive,
    Engines,
    Settings,
    About,
}

/// <summary>One entry of the navigation rail.</summary>
/// <param name="IconKey">Geometry resource key (Icon.*).</param>
/// <param name="LabelKey">Localization key (L.Nav.*).</param>
public sealed record NavEntry(PageKind Kind, string IconKey, string LabelKey, string Shortcut)
{
    public string Label => Loc.Get(LabelKey);

    public string AutomationId => "Nav." + Kind;
}

/// <summary>Navigation state. Page view-models are created lazily on first visit and cached.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<PageKind, object> _factory;
    private readonly Dictionary<PageKind, object> _pages = [];

    public ShellViewModel(Func<PageKind, object> pageFactory)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);
        _factory = pageFactory;
        Navigation =
        [
            new NavEntry(PageKind.Forge, "Icon.Forge", "L.Nav.Forge", "Ctrl+1"),
            new NavEntry(PageKind.Compare, "Icon.Compare", "L.Nav.Compare", "Ctrl+2"),
            new NavEntry(PageKind.Profiles, "Icon.Profiles", "L.Nav.Profiles", "Ctrl+3"),
            new NavEntry(PageKind.Archive, "Icon.Archive", "L.Nav.Archive", "Ctrl+4"),
            new NavEntry(PageKind.Engines, "Icon.Engines", "L.Nav.Engines", "Ctrl+5"),
            new NavEntry(PageKind.Settings, "Icon.Settings", "L.Nav.Settings", "Ctrl+6"),
            new NavEntry(PageKind.About, "Icon.About", "L.Nav.About", "Ctrl+7"),
        ];
        Current = PageKind.Forge;
        CurrentPage = PageFor(PageKind.Forge);
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public IReadOnlyList<NavEntry> Navigation { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CurrentEntry), nameof(CurrentIndex))]
    public partial PageKind Current { get; set; }

    /// <summary>Index of the current page in <see cref="Navigation"/> (two-way bindable by the navigation rail).</summary>
    public int CurrentIndex
    {
        get => (int)Current;
        set
        {
            if (value >= 0 && value < Navigation.Count)
            {
                Navigate(Navigation[value].Kind);
            }
        }
    }

    [ObservableProperty]
    public partial object CurrentPage { get; set; }

    [ObservableProperty]
    public partial bool IsPaletteOpen { get; set; }

    public NavEntry CurrentEntry => Navigation.First(n => n.Kind == Current);

    /// <summary>Localized title of the current page.</summary>
    public string Title => Loc.Get(CurrentEntry.LabelKey);

    /// <summary>Returns the page view-model, creating it on first use.</summary>
    public object PageFor(PageKind kind)
    {
        if (!_pages.TryGetValue(kind, out var page))
        {
            page = _factory(kind);
            _pages[kind] = page;
        }

        return page;
    }

    /// <summary>True when the page was created (visited) already.</summary>
    public bool IsCreated(PageKind kind) => _pages.ContainsKey(kind);

    [RelayCommand]
    public void Navigate(PageKind kind)
    {
        if (kind == Current)
        {
            return;
        }

        CurrentPage = PageFor(kind);
        Current = kind;
    }

    [RelayCommand]
    private void TogglePalette() => IsPaletteOpen = !IsPaletteOpen;
}
