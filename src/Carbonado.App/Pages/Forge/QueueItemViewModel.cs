using System.Windows.Media;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Carbonado.App.Pages.Forge;

public enum QueueItemStatus
{
    Pending,
    Working,
    Done,
    Kept,
    Failed,
    Cancelled,
}

/// <summary>One image in the Forge queue.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(string path, string? root = null, long originalBytes = 0)
    {
        Path = path;
        Root = root;
        FileName = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        OriginalBytes = originalBytes;
    }

    public string Path { get; }

    /// <summary>Folder the file was added from (for mirrored output trees), or null for single files.</summary>
    public string? Root { get; }

    public string FileName { get; }

    public string Folder { get; }

    public Guid? JobId { get; set; }

    public CompressionResult? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DimensionsText))]
    public partial int Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DimensionsText))]
    public partial int Height { get; set; }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalText), nameof(SavedFraction), nameof(SavedText))]
    public partial long OriginalBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputText), nameof(SavedFraction), nameof(SavedText))]
    public partial long OutputBytes { get; set; }

    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    public partial OutputFormat? Format { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Grade), nameof(ScoreText))]
    public partial double? Score { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Grade), nameof(ScoreText), nameof(IsVerifiedLossless))]
    public partial VerificationKind Verification { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished), nameof(IsWorking), nameof(StatusText), nameof(SavedText))]
    public partial QueueItemStatus Status { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? StageText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    public bool IsWorking => Status == QueueItemStatus.Working;

    public bool IsFinished => Status is QueueItemStatus.Done or QueueItemStatus.Kept or QueueItemStatus.Failed or QueueItemStatus.Cancelled;

    public bool IsVerifiedLossless => Verification is VerificationKind.PixelExact or VerificationKind.BitExactReconstruction;

    /// <summary>0..1 bytes saved (only for compressed items).</summary>
    public double SavedFraction =>
        Status == QueueItemStatus.Done && OriginalBytes > 0 && OutputBytes > 0 ? Math.Max(0, 1.0 - ((double)OutputBytes / OriginalBytes)) : 0;

    public string Grade => IsVerifiedLossless ? "FL" : Score is { } s ? Services.FormatText.GradeFor(s) : string.Empty;

    public string DimensionsText => Width > 0 ? $"{Width} × {Height}" : string.Empty;

    public string OriginalText => OriginalBytes > 0 ? Services.FormatText.Bytes(OriginalBytes) : string.Empty;

    public string OutputText => OutputBytes > 0 ? Services.FormatText.Bytes(OutputBytes) : string.Empty;

    public string FormatText => Format is { } f ? FormatInfo.DisplayName(f) : string.Empty;

    public string ScoreText => IsVerifiedLossless
        ? Loc.Get(Verification == VerificationKind.BitExactReconstruction ? "L.Verify.BitExact" : "L.Verify.PixelExact")
        : Score is { } s ? Services.FormatText.Score(s) : string.Empty;

    public string SavedText => Status switch
    {
        QueueItemStatus.Done => "−" + Services.FormatText.Percent(SavedFraction),
        QueueItemStatus.Kept => Loc.Get("L.Status.KeptShort"),
        _ => string.Empty,
    };

    public string StatusText => Status switch
    {
        QueueItemStatus.Pending => Loc.Get("L.Status.Pending"),
        QueueItemStatus.Working => StageText ?? Loc.Get("L.Status.Working"),
        QueueItemStatus.Done => Loc.Get("L.Status.Done"),
        QueueItemStatus.Kept => Loc.Get("L.Status.Kept"),
        QueueItemStatus.Failed => Error ?? Loc.Get("L.Status.Failed"),
        QueueItemStatus.Cancelled => Loc.Get("L.Status.Cancelled"),
        _ => string.Empty,
    };

    /// <summary>Back to pending (retry).</summary>
    public void Reset()
    {
        JobId = null;
        Result = null;
        OutputBytes = 0;
        OutputPath = null;
        Format = null;
        Score = null;
        Verification = VerificationKind.None;
        Error = null;
        StageText = null;
        Progress = 0;
        Status = QueueItemStatus.Pending;
    }

    public void Apply(JobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (IsFinished)
        {
            return;
        }

        Status = QueueItemStatus.Working;
        Progress = Math.Clamp(progress.Fraction, 0, 1);
        StageText = DescribeStage(progress);
        OnPropertyChanged(nameof(StatusText));
    }

    public void Apply(CompressionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        if (result.OriginalBytes > 0)
        {
            OriginalBytes = result.OriginalBytes;
        }

        if (result.Width > 0)
        {
            Width = result.Width;
            Height = result.Height;
        }

        OutputBytes = result.OutputBytes;
        OutputPath = result.OutputPath;
        Format = result.Format;
        Score = result.Score;
        Verification = result.Verification;
        Error = result.Error;
        Progress = 1;
        StageText = null;
        Status = result.Outcome switch
        {
            JobOutcome.Compressed => QueueItemStatus.Done,
            JobOutcome.KeptOriginal or JobOutcome.Skipped => QueueItemStatus.Kept,
            JobOutcome.Cancelled => QueueItemStatus.Cancelled,
            _ => QueueItemStatus.Failed,
        };
    }

    /// <summary>Re-raises every computed text (after a language switch).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(SavedText));
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(OutputText));
    }

    internal static string DescribeStage(JobProgress p)
    {
        string stage = Loc.Get("L.Stage." + p.Stage);
        if (p.Stage == JobStage.Encoding && p.CandidateId is { } candidate)
        {
            return p.CandidateScore is { } score
                ? Loc.Format("L.Stage.CandidateScored", candidate, Services.FormatText.Score(score))
                : Loc.Format("L.Stage.Candidate", candidate);
        }

        return stage;
    }
}
