using System.IO;
using Carbonado.App.Services;
using Carbonado.Core.Engine;

namespace Carbonado.App.Tests.TestSupport;

/// <summary>Runs posted actions synchronously (single-threaded tests).</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

public sealed class FakeBatch : IBatchProcessor
{
    public List<CompressionJob> Enqueued { get; } = [];

    public List<Guid> Cancelled { get; } = [];

    public int Starts { get; private set; }

    public BatchState State { get; private set; } = BatchState.Idle;

    public int Pending => Enqueued.Count;

    public int Running => 0;

    public event EventHandler<JobProgress>? Progress;

    public event EventHandler<CompressionResult>? JobCompleted;

    public event EventHandler<BatchSummary>? BatchCompleted;

    public event EventHandler<BatchState>? StateChanged;

    public void Enqueue(CompressionJob job) => Enqueued.Add(job);

    public void Start()
    {
        Starts++;
        SetState(BatchState.Running);
    }

    public void Pause() => SetState(BatchState.Paused);

    public void Resume() => SetState(BatchState.Running);

    public void Cancel(Guid jobId) => Cancelled.Add(jobId);

    public void CancelAll() => SetState(BatchState.Cancelling);

    public void RaiseProgress(JobProgress p) => Progress?.Invoke(this, p);

    public void RaiseCompleted(CompressionResult r) => JobCompleted?.Invoke(this, r);

    public void RaiseBatchCompleted(BatchSummary s)
    {
        BatchCompleted?.Invoke(this, s);
        SetState(BatchState.Idle);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetState(BatchState s)
    {
        State = s;
        StateChanged?.Invoke(this, s);
    }
}

public sealed class FakeThumbnails : IThumbnailService
{
    public Task<ThumbnailInfo?> LoadAsync(string path, int maxSize, CancellationToken ct) =>
        Task.FromResult<ThumbnailInfo?>(new ThumbnailInfo(null, 640, 480, 1234));
}

public sealed class FakeDialogs : IDialogService
{
    public ConfirmResult NextConfirm { get; set; } = ConfirmResult.Primary;

    public List<string> Confirms { get; } = [];

    public IReadOnlyList<string> FilesToPick { get; set; } = [];

    public string? FolderToPick { get; set; }

    public Task<ConfirmResult> ConfirmAsync(string title, string message, string primary, string? secondary = null, bool danger = false)
    {
        Confirms.Add(title);
        return Task.FromResult(NextConfirm);
    }

    public IReadOnlyList<string> PickFiles() => FilesToPick;

    public string? PickFolder(string? initial = null) => FolderToPick;
}

public sealed class FakeToasts : IToastService
{
    public List<(string Title, string Message, ToastKind Kind)> Shown { get; } = [];

    public void Show(string title, string message, ToastKind kind = ToastKind.Info) => Shown.Add((title, message, kind));
}

public sealed class FakeSound : ISoundService
{
    public int Plays { get; private set; }

    public void PlayCompletion() => Plays++;
}

public sealed class FakeShell : IShellLauncher
{
    public List<string> Opened { get; } = [];

    public void OpenFolder(string folder, string? selectFile = null) => Opened.Add(selectFile ?? folder);

    public void OpenFile(string path) => Opened.Add(path);

    public void OpenUrl(string url) => Opened.Add(url);
}

/// <summary>Temporary folder for app tests.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "carbonado-app-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Touch(string relative, int bytes = 10)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}
