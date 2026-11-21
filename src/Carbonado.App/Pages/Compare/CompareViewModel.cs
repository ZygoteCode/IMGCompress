using System.Collections.ObjectModel;
using System.Windows.Media;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Compare;

public enum ComparisonMode
{
    Split,
    SideBySide,
    Flicker,
    Heatmap,
}

/// <summary>An original/output pair that can be compared.</summary>
public sealed record ComparisonSource(string OriginalPath, string OutputPath, CompressionResult? Result)
{
    public string Title => Path.GetFileName(OriginalPath);
}

/// <summary>Decoded images for display (frozen, any thread).</summary>
public sealed record ComparisonImages(ImageSource Before, ImageSource After, int Width, int Height);

/// <summary>Decodes originals/outputs (any format, via the engine's loader) and builds SSIMULACRA2 error maps.</summary>
public interface IComparisonLoader
{
    Task<ComparisonImages> LoadAsync(string original, string output, CancellationToken ct);

    /// <summary>Error heat-map (0 = identical … 1 = strongest distortion) colorized, plus the measured score.</summary>
    Task<(ImageSource Map, double Score)> HeatmapAsync(string original, string output, CancellationToken ct);
}

/// <summary>Compare page: split / side-by-side / flicker / error-map views of an original and its result.</summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly IComparisonLoader _loader;
    private CancellationTokenSource? _loadCts;

    public CompareViewModel(IComparisonLoader loader)
    {
        _loader = loader;
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<ComparisonSource> Sources { get; } = [];

    public IReadOnlyList<ComparisonMode> Modes { get; } = [ComparisonMode.Split, ComparisonMode.SideBySide, ComparisonMode.Flicker, ComparisonMode.Heatmap];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(OriginalSizeText), nameof(OutputSizeText), nameof(FormatText), nameof(EncoderText), nameof(ScoreText), nameof(VerificationText), nameof(SavedText))]
    public partial ComparisonSource? Selected { get; set; }

    [ObservableProperty]
    public partial ComparisonMode Mode { get; set; } = ComparisonMode.Split;

    [ObservableProperty]
    public partial ImageSource? Before { get; set; }

    [ObservableProperty]
    public partial ImageSource? After { get; set; }

    [ObservableProperty]
    public partial ImageSource? Heatmap { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    public partial double? MeasuredScore { get; set; }

    [ObservableProperty]
    public partial int PixelWidth { get; set; }

    [ObservableProperty]
    public partial int PixelHeight { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Sources.Count == 0;

    public string OriginalSizeText => Selected?.Result is { } r ? FormatTextOf(r.OriginalBytes) : string.Empty;

    public string OutputSizeText => Selected?.Result is { } r ? FormatTextOf(r.OutputBytes) : string.Empty;

    public string SavedText => Selected?.Result is { SavedFraction: > 0 } r ? "−" + Services.FormatText.Percent(r.SavedFraction) : string.Empty;

    public string FormatText => Selected?.Result?.Format is { } f ? FormatInfo.DisplayName(f) : string.Empty;

    public string EncoderText => Selected?.Result?.EncoderId ?? string.Empty;

    public string ScoreText => (MeasuredScore ?? Selected?.Result?.Score) is { } s ? Services.FormatText.Score(s) : string.Empty;

    public string VerificationText => Selected?.Result?.Verification switch
    {
        VerificationKind.PixelExact => Loc.Get("L.Verify.PixelExact"),
        VerificationKind.BitExactReconstruction => Loc.Get("L.Verify.BitExact"),
        VerificationKind.PerceptualScore => "SSIMULACRA2",
        _ => string.Empty,
    };

    /// <summary>Adds (or re-selects) a pair and selects it.</summary>
    public void Show(ComparisonSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var existing = Sources.FirstOrDefault(s => string.Equals(s.OutputPath, source.OutputPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Sources.Insert(0, source);
            existing = source;
            OnPropertyChanged(nameof(IsEmpty));
        }

        Selected = existing;
    }

    partial void OnSelectedChanged(ComparisonSource? value) => _ = LoadAsync(value);

    partial void OnModeChanged(ComparisonMode value)
    {
        if (value == ComparisonMode.Heatmap && Heatmap is null && Selected is not null)
        {
            _ = LoadHeatmapAsync(Selected);
        }
    }

    [RelayCommand]
    private void SetMode(ComparisonMode mode) => Mode = mode;

    private async Task LoadAsync(ComparisonSource? source)
    {
        _loadCts?.Cancel();
        Before = After = Heatmap = null;
        MeasuredScore = null;
        Error = null;
        if (source is null)
        {
            return;
        }

        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var images = await _loader.LoadAsync(source.OriginalPath, source.OutputPath, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Before = images.Before;
            After = images.After;
            PixelWidth = images.Width;
            PixelHeight = images.Height;
            if (Mode == ComparisonMode.Heatmap)
            {
                await LoadHeatmapAsync(source).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = Loc.Format("L.Compare.Error", e.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadHeatmapAsync(ComparisonSource source)
    {
        var token = _loadCts?.Token ?? CancellationToken.None;
        try
        {
            IsLoading = true;
            var (map, score) = await _loader.HeatmapAsync(source.OriginalPath, source.OutputPath, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && ReferenceEquals(Selected, source))
            {
                Heatmap = map;
                MeasuredScore = score;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Error = Loc.Format("L.Compare.Error", e.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FormatTextOf(long bytes) => bytes > 0 ? Services.FormatText.Bytes(bytes) : string.Empty;
}
