using System.Collections.ObjectModel;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Carbonado.App.Pages.Profiles;

/// <summary>A selectable output format in the editor.</summary>
public sealed partial class FormatToggle(OutputFormat format, bool isOn) : ObservableObject
{
    public OutputFormat Format { get; } = format;

    public string Label => FormatInfo.DisplayName(Format);

    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;
}

/// <summary>Profiles page: browse built-ins, duplicate, edit and delete custom profiles, choose the active one.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileCatalog _catalog;
    private readonly IDialogService _dialogs;
    private bool _loading;

    public ProfilesViewModel(IProfileCatalog catalog, IDialogService dialogs)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        Formats = [.. new[] { OutputFormat.Jxl, OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png }.Select(f => new FormatToggle(f, false))];
        foreach (var f in Formats)
        {
            f.PropertyChanged += (_, _) => MarkDirty();
        }

        Reload();
        Selected = Profiles.FirstOrDefault(p => p.Id == catalog.Active.Id) ?? Profiles.FirstOrDefault();
        catalog.Changed += (_, _) => Reload();
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ObservableCollection<CompressionProfile> Profiles { get; } = [];

    public IReadOnlyList<FormatToggle> Formats { get; }

    public IReadOnlyList<QualityMode> Modes { get; } = [QualityMode.TargetScore, QualityMode.Lossless, QualityMode.TargetSize];

    public IReadOnlyList<FormatStrategy> Strategies { get; } = [FormatStrategy.Auto, FormatStrategy.KeepSource, FormatStrategy.Fixed];

    public IReadOnlyList<EffortLevel> Efforts { get; } = [EffortLevel.Fast, EffortLevel.Balanced, EffortLevel.Max, EffortLevel.Insane];

    public IReadOnlyList<MetadataPolicy> MetadataPolicies { get; } =
        [MetadataPolicy.StripAll, MetadataPolicy.KeepColorProfile, MetadataPolicy.KeepAllButGps, MetadataPolicy.KeepAll];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(IsActive), nameof(CanDelete))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(SetActiveCommand))]
    public partial CompressionProfile? Selected { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScoreMode), nameof(IsSizeMode), nameof(IsLossless))]
    public partial QualityMode EditMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Grade), nameof(GradeExplanation))]
    public partial double EditTargetScore { get; set; } = 85;

    [ObservableProperty]
    public partial double EditTargetKilobytes { get; set; } = 300;

    [ObservableProperty]
    public partial FormatStrategy EditStrategy { get; set; }

    [ObservableProperty]
    public partial EffortLevel EditEffort { get; set; }

    [ObservableProperty]
    public partial MetadataPolicy EditMetadata { get; set; }

    [ObservableProperty]
    public partial bool EditResize { get; set; }

    [ObservableProperty]
    public partial int EditMaxWidth { get; set; } = 2048;

    [ObservableProperty]
    public partial int EditMaxHeight { get; set; } = 2048;

    [ObservableProperty]
    public partial bool EditNeverLarger { get; set; } = true;

    [ObservableProperty]
    public partial bool EditJpegTranscode { get; set; } = true;

    [ObservableProperty]
    public partial bool EditCleanInvisible { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    public bool IsEditable => Selected is { BuiltIn: false };

    public bool CanDelete => IsEditable;

    public bool IsActive => Selected is not null && Selected.Id == _catalog.Active.Id;

    public bool IsScoreMode => EditMode == QualityMode.TargetScore;

    public bool IsSizeMode => EditMode == QualityMode.TargetSize;

    public bool IsLossless => EditMode == QualityMode.Lossless;

    public string Grade => FormatText.GradeFor(EditTargetScore);

    public string GradeExplanation => Loc.Get("L.Grade." + Grade);

    partial void OnSelectedChanged(CompressionProfile? value)
    {
        _loading = true;
        try
        {
            if (value is null)
            {
                return;
            }

            EditName = value.DisplayName(Loc.Language);
            EditMode = value.Mode;
            EditTargetScore = value.Mode == QualityMode.Lossless ? 100 : value.TargetScore;
            EditTargetKilobytes = (value.TargetBytes ?? (300 * 1024)) / 1024.0;
            EditStrategy = value.Strategy;
            EditEffort = value.Effort;
            EditMetadata = value.Metadata;
            EditResize = value.Resize is not null;
            EditMaxWidth = value.Resize?.MaxWidth ?? 2048;
            EditMaxHeight = value.Resize?.MaxHeight ?? 2048;
            EditNeverLarger = value.NeverLarger;
            EditJpegTranscode = value.AllowJpegTranscode;
            EditCleanInvisible = value.CleanInvisiblePixels;
            foreach (var f in Formats)
            {
                f.IsOn = value.Formats.Contains(f.Format);
            }
        }
        finally
        {
            _loading = false;
            IsDirty = false;
            ValidationError = null;
        }
    }

    /// <summary>Builds a profile from the editor fields (id/built-in flags from <paramref name="basis"/>).</summary>
    public CompressionProfile BuildFromEditor(CompressionProfile basis)
    {
        var formats = Formats.Where(f => f.IsOn).Select(f => f.Format).ToList();
        return basis with
        {
            Name = EditName.Trim(),
            NameIt = EditName.Trim(),
            Mode = EditMode,
            TargetScore = EditMode == QualityMode.Lossless ? 100 : Math.Round(Math.Clamp(EditTargetScore, 30, 99), 1),
            TargetBytes = EditMode == QualityMode.TargetSize ? (long)(Math.Max(1, EditTargetKilobytes) * 1024) : null,
            Strategy = EditStrategy,
            Formats = formats,
            Effort = EditEffort,
            Metadata = EditMetadata,
            Resize = EditResize ? new ResizeSpec(Math.Max(16, EditMaxWidth), Math.Max(16, EditMaxHeight)) : null,
            NeverLarger = EditNeverLarger,
            AllowJpegTranscode = EditJpegTranscode,
            CleanInvisiblePixels = EditCleanInvisible,
            Grade = EditMode == QualityMode.Lossless ? "FL" : EditMode == QualityMode.TargetSize ? string.Empty : FormatText.GradeFor(EditTargetScore),
        };
    }

    /// <summary>Null when valid, else a localization key describing the problem.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            return "L.Profiles.Error.Name";
        }

        if (EditStrategy != FormatStrategy.KeepSource && !Formats.Any(f => f.IsOn))
        {
            return "L.Profiles.Error.Formats";
        }

        return null;
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null)
        {
            return;
        }

        string id = _catalog is ProfileCatalog pc ? pc.NewIdFor(Selected) : Selected.Id + "-" + Guid.NewGuid().ToString("N")[..4];
        string name = Loc.Format("L.Profiles.CopyOf", Selected.DisplayName(Loc.Language));
        var copy = Selected with { Id = id, Name = name, NameIt = name, BuiltIn = false };
        _catalog.SaveCustom(copy);
        Selected = Profiles.FirstOrDefault(p => p.Id == id);
    }

    private bool CanSave() => IsDirty && IsEditable;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Selected is null || Selected.BuiltIn)
        {
            return;
        }

        string? error = Validate();
        ValidationError = error is null ? null : Loc.Get(error);
        if (error is not null)
        {
            return;
        }

        string id = Selected.Id;
        _catalog.SaveCustom(BuildFromEditor(Selected));
        Selected = Profiles.FirstOrDefault(p => p.Id == id);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is not { BuiltIn: false } victim)
        {
            return;
        }

        var answer = await _dialogs.ConfirmAsync(
            Loc.Get("L.Profiles.Delete.Title"), Loc.Format("L.Profiles.Delete.Message", victim.DisplayName(Loc.Language)),
            Loc.Get("L.Common.Delete"), Loc.Get("L.Common.Cancel"), danger: true).ConfigureAwait(true);
        if (answer == ConfirmResult.Primary)
        {
            _catalog.DeleteCustom(victim.Id);
            Selected = Profiles.FirstOrDefault(p => p.Id == _catalog.Active.Id) ?? Profiles.FirstOrDefault();
        }
    }

    private bool CanSetActive() => Selected is not null && !IsActive;

    [RelayCommand(CanExecute = nameof(CanSetActive))]
    private void SetActive()
    {
        if (Selected is not null)
        {
            _catalog.SetActive(Selected.Id);
            OnPropertyChanged(nameof(IsActive));
            SetActiveCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnEditNameChanged(string value) => MarkDirty();

    partial void OnEditModeChanged(QualityMode value) => MarkDirty();

    partial void OnEditTargetScoreChanged(double value) => MarkDirty();

    partial void OnEditTargetKilobytesChanged(double value) => MarkDirty();

    partial void OnEditStrategyChanged(FormatStrategy value) => MarkDirty();

    partial void OnEditEffortChanged(EffortLevel value) => MarkDirty();

    partial void OnEditMetadataChanged(MetadataPolicy value) => MarkDirty();

    partial void OnEditResizeChanged(bool value) => MarkDirty();

    partial void OnEditMaxWidthChanged(int value) => MarkDirty();

    partial void OnEditMaxHeightChanged(int value) => MarkDirty();

    partial void OnEditNeverLargerChanged(bool value) => MarkDirty();

    partial void OnEditJpegTranscodeChanged(bool value) => MarkDirty();

    partial void OnEditCleanInvisibleChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    private void Reload()
    {
        string? keep = Selected?.Id;
        Profiles.Clear();
        foreach (var p in _catalog.All)
        {
            Profiles.Add(p);
        }

        if (keep is not null)
        {
            Selected = Profiles.FirstOrDefault(p => p.Id == keep) ?? Profiles.FirstOrDefault();
        }

        OnPropertyChanged(nameof(IsActive));
        SetActiveCommand.NotifyCanExecuteChanged();
    }
}
