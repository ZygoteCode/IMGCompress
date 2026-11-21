using System.Collections.ObjectModel;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Engines;

/// <summary>Located encoders and what they enable.</summary>
public interface IToolCatalog
{
    IReadOnlyList<ToolInfo> Tools { get; }

    /// <summary>Folder holding the bundled tools (for "open folder").</summary>
    string ToolsFolder { get; }

    /// <summary>Output formats that have at least one working encoder.</summary>
    IReadOnlyCollection<OutputFormat> AvailableFormats { get; }

    event EventHandler? Changed;

    Task RefreshAsync(CancellationToken ct);
}

public enum EngineStatus
{
    Verified,
    Unverified,
    Modified,
    Missing,
}

/// <summary>One row of the engines table.</summary>
public sealed record EngineRow(ToolId Id, ToolInfo? Info)
{
    public string Name => Id.ToString().ToLowerInvariant();

    public string Role => Loc.Get("L.Engines.Role." + Id);

    public string Version => Info?.Version ?? string.Empty;

    public string Location => Info?.Path ?? string.Empty;

    public string Package => Info?.Package ?? string.Empty;

    public EngineStatus Status => Info?.Integrity switch
    {
        null => EngineStatus.Missing,
        ToolIntegrity.Verified => EngineStatus.Verified,
        ToolIntegrity.Mismatch => EngineStatus.Modified,
        _ => EngineStatus.Unverified,
    };

    public string StatusText => Status switch
    {
        EngineStatus.Verified => Loc.Get("L.Engines.Verified"),
        EngineStatus.Modified => Loc.Get("L.Engines.Mismatch"),
        EngineStatus.Missing => Loc.Get("L.Engines.Missing"),
        _ => Loc.Get("L.Engines.Unknown"),
    };
}

/// <summary>A format chip with availability.</summary>
public sealed record FormatAvailability(OutputFormat Format, bool Available)
{
    public string Label => FormatInfo.DisplayName(Format);
}

/// <summary>Engines page: every encoder with version, location and SHA-256 integrity.</summary>
public sealed partial class EnginesViewModel : ObservableObject
{
    private readonly IToolCatalog _catalog;
    private readonly IShellLauncher _shell;
    private readonly IUiDispatcher _ui;

    public EnginesViewModel(IToolCatalog catalog, IShellLauncher shell, IUiDispatcher ui)
    {
        _catalog = catalog;
        _shell = shell;
        _ui = ui;
        _catalog.Changed += (_, _) => _ui.Post(Rebuild);
        Loc.LanguageChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<EngineRow> Rows { get; } = [];

    public ObservableCollection<FormatAvailability> Formats { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial int VerifiedCount { get; set; }

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsScanning = true;
        try
        {
            await _catalog.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            Rebuild();
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void OpenFolder() => _shell.OpenFolder(_catalog.ToolsFolder);

    private void Rebuild()
    {
        var byId = _catalog.Tools.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        Rows.Clear();
        foreach (var id in Enum.GetValues<ToolId>())
        {
            Rows.Add(new EngineRow(id, byId.GetValueOrDefault(id)));
        }

        Formats.Clear();
        foreach (var f in new[] { OutputFormat.Jxl, OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png })
        {
            Formats.Add(new FormatAvailability(f, _catalog.AvailableFormats.Contains(f)));
        }

        VerifiedCount = Rows.Count(r => r.Status == EngineStatus.Verified);
        HasProblems = Rows.Any(r => r.Status == EngineStatus.Modified)
                      || Rows.Any(r => r.Status == EngineStatus.Missing && r.Id is ToolId.Cjxl or ToolId.Avifenc or ToolId.Cwebp or ToolId.Djxl);
    }
}
