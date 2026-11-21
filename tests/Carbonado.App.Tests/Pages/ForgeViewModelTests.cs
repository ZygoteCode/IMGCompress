using Carbonado.App.Localization;
using Carbonado.App.Pages.Forge;
using Carbonado.App.Services;
using Carbonado.App.Tests.TestSupport;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Persistence;

namespace Carbonado.App.Tests.Pages;

public sealed class ForgeViewModelTests : IDisposable
{
    private readonly FakeBatch _batch = new();
    private readonly AppSettings _settings = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToasts _toasts = new();
    private readonly FakeSound _sound = new();
    private readonly FakeShell _shell = new();
    private readonly TempFolder _dir = new();
    private readonly ForgeViewModel _vm;

    public ForgeViewModelTests()
    {
        Loc.SetLookupOverride(k => k);
        _vm = new ForgeViewModel(_batch, new ProfileCatalog(_settings, _ => { }), () => _settings.Output,
            new FakeThumbnails(), new ImmediateDispatcher(), _dialogs, _shell, _toasts, _sound);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _dir.Dispose();
        Loc.SetLookupOverride(null);
    }

    [Fact]
    public async Task AddPaths_DedupesExpandsFoldersAndSkipsOutputsAndNonImages()
    {
        string a = _dir.Touch("a.png", 100);
        _dir.Touch(@"sub\b.jpg", 200);
        _dir.Touch(@"sub\deeper\c.webp", 300);
        _dir.Touch(@"Carbonado\a.avif", 50);
        _dir.Touch("notes.txt");

        int added = await _vm.AddPathsAsync([_dir.Path, a, a]);

        Assert.Equal(3, added);
        Assert.Equal(3, _vm.Items.Count);
        Assert.All(_vm.Items, i => Assert.Equal(_dir.Path, i.Root));
        Assert.Equal(100, _vm.Items.Single(i => i.FileName == "a.png").OriginalBytes);
        Assert.Equal(640, _vm.Items[0].Width);
        Assert.True(_vm.HasItems);
        Assert.Equal(3, _vm.PendingCount);

        Assert.Equal(0, await _vm.AddPathsAsync([a]));
    }

    [Fact]
    public async Task Start_EnqueuesPendingWithEffectiveProfile()
    {
        await _vm.AddPathsAsync([_dir.Touch("x.png"), _dir.Touch("y.png")]);
        _vm.SelectedFormat = _vm.FormatChoices.Single(f => f.Format == OutputFormat.Avif);
        _vm.EffortOverride = EffortLevel.Max;

        Assert.True(_vm.StartCommand.CanExecute(null));
        _vm.StartCommand.Execute(null);

        Assert.Equal(2, _batch.Enqueued.Count);
        Assert.Equal(1, _batch.Starts);
        var profile = _batch.Enqueued[0].Profile;
        Assert.Equal(FormatStrategy.Fixed, profile.Strategy);
        Assert.Equal([OutputFormat.Avif], profile.Formats);
        Assert.Equal(EffortLevel.Max, profile.Effort);
        Assert.Equal("vs", profile.Id);
        Assert.True(_vm.IsRunning);
        Assert.False(_vm.StartCommand.CanExecute(null));
        Assert.All(_vm.Items, i => Assert.Equal(QueueItemStatus.Working, i.Status));
    }

    [Fact]
    public async Task JpegOverride_FallsBackToWebPForTransparency()
    {
        await _vm.AddPathsAsync([_dir.Touch("x.png")]);
        _vm.SelectedFormat = _vm.FormatChoices.Single(f => f.Format == OutputFormat.Jpeg);
        Assert.Equal([OutputFormat.Jpeg, OutputFormat.WebP], _vm.EffectiveProfile().Formats);
    }

    [Fact]
    public async Task ProgressAndCompletion_UpdateItemsTotalsToastAndSound()
    {
        await _vm.AddPathsAsync([_dir.Touch("p.png", 1000), _dir.Touch("q.png", 2000), _dir.Touch("r.png", 500)]);
        _vm.StartCommand.Execute(null);
        var jobs = _batch.Enqueued;

        _batch.RaiseProgress(new JobProgress(jobs[0].Id, JobStage.Encoding, 0.5, null, "avif.avifenc", 88.4, 400));
        var p = _vm.Items[0];
        Assert.Equal(QueueItemStatus.Working, p.Status);
        Assert.Equal(0.5, p.Progress);
        Assert.StartsWith("p.png · L.Stage.CandidateScored", _vm.ActivityText, StringComparison.Ordinal);

        _batch.RaiseCompleted(Result(jobs[0], JobOutcome.Compressed, 1000, 250, OutputFormat.Avif, 86.1));
        _batch.RaiseCompleted(Result(jobs[1], JobOutcome.KeptOriginal, 2000, 2000, null, null));
        _batch.RaiseCompleted(Result(jobs[2], JobOutcome.Failed, 500, 0, null, null, "corrupt"));

        Assert.Equal(QueueItemStatus.Done, _vm.Items[0].Status);
        Assert.Equal(QueueItemStatus.Kept, _vm.Items[1].Status);
        Assert.Equal(QueueItemStatus.Failed, _vm.Items[2].Status);
        Assert.Equal("corrupt", _vm.Items[2].StatusText);
        Assert.Equal("VS", _vm.Items[0].Grade);
        Assert.Equal(0.75, _vm.Items[0].SavedFraction, 3);
        Assert.Equal(3000, _vm.TotalOriginalBytes);
        Assert.Equal(2250, _vm.TotalOutputBytes);
        Assert.Equal(750, _vm.SavedBytes);
        Assert.Equal(0.25, _vm.SavedFraction, 3);
        Assert.Equal(1, _vm.CompletedCount);
        Assert.Equal(1, _vm.KeptCount);
        Assert.Equal(1, _vm.FailedCount);
        Assert.Equal(1.0, _vm.OverallProgress, 3);

        _batch.RaiseBatchCompleted(new BatchSummary(3, 1, 1, 1, 0, 3000, 2250, TimeSpan.FromSeconds(3)));
        Assert.False(_vm.IsRunning);
        var toast = Assert.Single(_toasts.Shown);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal(1, _sound.Plays);
    }

    [Fact]
    public async Task Remove_WorkingItem_CancelsItsJob_AndRetryResets()
    {
        await _vm.AddPathsAsync([_dir.Touch("p.png"), _dir.Touch("q.png")]);
        _vm.StartCommand.Execute(null);
        var first = _vm.Items[0];
        Guid id = first.JobId!.Value;

        _vm.RemoveCommand.Execute(first);
        Assert.Equal([id], _batch.Cancelled);
        Assert.Single(_vm.Items);

        var second = _vm.Items[0];
        _batch.RaiseCompleted(Result(_batch.Enqueued[1], JobOutcome.Failed, 10, 0, null, null, "boom"));
        _vm.RetryCommand.Execute(second);
        Assert.Equal(QueueItemStatus.Pending, second.Status);
        Assert.Null(second.JobId);
    }

    [Fact]
    public async Task Clear_WhileRunning_AsksConfirmationAndCancels()
    {
        await _vm.AddPathsAsync([_dir.Touch("p.png")]);
        _vm.StartCommand.Execute(null);
        _dialogs.NextConfirm = ConfirmResult.Secondary;
        await _vm.ClearCommand.ExecuteAsync(null);
        Assert.Single(_vm.Items);

        _dialogs.NextConfirm = ConfirmResult.Primary;
        await _vm.ClearCommand.ExecuteAsync(null);
        Assert.Empty(_vm.Items);
        Assert.Equal(BatchState.Cancelling, _batch.State);
        Assert.Equal(2, _dialogs.Confirms.Count);
    }

    [Fact]
    public void ProfileSelection_PersistsActiveProfile()
    {
        _vm.SelectedProfile = _vm.Profiles.Single(p => p.Id == "vvs");
        Assert.Equal("vvs", _settings.ActiveProfileId);
    }

    private static CompressionResult Result(CompressionJob job, JobOutcome outcome, long original, long output, OutputFormat? format, double? score, string? error = null) =>
        new(job.Id, job.InputPath, outcome == JobOutcome.Compressed ? job.InputPath + ".avif" : null, outcome, original, output, format,
            format is null ? null : "avif.avifenc", score, score is null ? VerificationKind.None : VerificationKind.PerceptualScore,
            TimeSpan.FromSeconds(1), [], error, 640, 480);
}
