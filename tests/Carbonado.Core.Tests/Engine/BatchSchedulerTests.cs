using System.Collections.Concurrent;
using System.Diagnostics;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tests.Tools;
using static Carbonado.Core.Tests.Engine.EngineTestSupport;

namespace Carbonado.Core.Tests.Engine;

public class BatchSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Scheduler_RunsAllJobs_RaisesBatchCompleted()
    {
        var engine = new FakeEngine((job, _, _, _) => Task.FromResult(job.InputPath switch
        {
            var p when p.EndsWith("0") => Result(job, JobOutcome.Compressed, 1000, 400),
            var p when p.EndsWith("1") => Result(job, JobOutcome.KeptOriginal, 500, 500),
            var p when p.EndsWith("2") => CompressionResult.Fail(job, "broken", 300),
            _ => Result(job, JobOutcome.Skipped, 200, 0),
        }));
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 3), 8, 1L << 30);
        var events = new EventLog(scheduler);
        var jobs = Enumerable.Range(0, 12).Select(i => Job($"img{i % 4}")).ToList();

        jobs.ForEach(scheduler.Enqueue);
        Assert.Equal(12, scheduler.Pending);
        scheduler.Start();
        BatchSummary summary = await events.Batch.Task.WaitAsync(Timeout, Ct);

        Assert.Equal(new BatchSummary(12, 3, 6, 3, 0, 3 * (1000 + 500), 3 * (400 + 500), summary.Elapsed), summary);
        Assert.InRange(engine.MaxConcurrent, 1, 3);
        Assert.Equal(12, engine.Calls);
        await events.WaitForStateAsync(BatchState.Idle);
        Assert.Equal((BatchState.Idle, 0, 0), (scheduler.State, scheduler.Pending, scheduler.Running));

        // Every job is reported once, all before the batch summary, and the states went Running → Idle.
        var log = events.Snapshot();
        Assert.Equal(jobs.Select(j => j.Id).Order(), log.OfType<CompressionResult>().Select(r => r.JobId).Order());
        Assert.True(log.FindLastIndex(e => e is CompressionResult) < log.FindIndex(e => e is BatchSummary));
        Assert.Equal([BatchState.Running, BatchState.Idle], log.OfType<BatchState>());
    }

    [Fact]
    public async Task Scheduler_StartWithEmptyQueue_DoesNothing()
    {
        await using var scheduler = new BatchScheduler(new FakeEngine(), new EngineOptions(), 4, 1L << 30);
        var events = new EventLog(scheduler);

        scheduler.Start();
        await Task.Delay(100, Ct);

        Assert.Equal(BatchState.Idle, scheduler.State);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task Scheduler_PauseResume()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 1), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var jobs = Enumerable.Range(0, 4).Select(i => Job($"p{i}")).ToList();
        jobs.ForEach(scheduler.Enqueue);

        scheduler.Start();
        await engine.WaitStartedAsync(1);
        scheduler.Pause();
        Assert.Equal(BatchState.Paused, scheduler.State);
        engine.Release(jobs[0].Id);
        await events.WaitForCompletedAsync(1);

        // Paused: the running job finished, nothing new starts.
        await Task.Delay(250, Ct);
        Assert.Equal(1, engine.Calls);
        Assert.Equal((3, 0), (scheduler.Pending, scheduler.Running));

        scheduler.Resume();
        foreach (var job in jobs.Skip(1))
        {
            engine.Release(job.Id);
        }

        var summary = await events.Batch.Task.WaitAsync(Timeout, Ct);
        await events.WaitForStateAsync(BatchState.Idle);
        Assert.Equal(4, summary.Compressed);
        Assert.Equal([BatchState.Running, BatchState.Paused, BatchState.Running, BatchState.Idle], events.Snapshot().OfType<BatchState>());
    }

    [Fact]
    public async Task Scheduler_StartWhilePaused_Resumes()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 1), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var a = Job("a");
        var b = Job("b");
        scheduler.Enqueue(a);
        scheduler.Enqueue(b);
        scheduler.Start();
        await engine.WaitStartedAsync(1);
        scheduler.Pause();
        engine.Release(a.Id);
        await events.WaitForCompletedAsync(1);

        scheduler.Start();
        engine.Release(b.Id);

        Assert.Equal(2, (await events.Batch.Task.WaitAsync(Timeout, Ct)).Total);
    }

    [Fact]
    public async Task Scheduler_CancelOne()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 1), 4, 1L << 30);
        var events = new EventLog(scheduler);
        CompressionJob running = Job("running"), queued = Job("queued"), last = Job("last");
        scheduler.Enqueue(running);
        scheduler.Enqueue(queued);
        scheduler.Enqueue(last);
        scheduler.Start();
        await engine.WaitStartedAsync(1);

        // A queued job completes at once, without ever reaching the engine.
        scheduler.Cancel(queued.Id);
        var first = await events.WaitForCompletedAsync(1);
        Assert.Equal((queued.Id, JobOutcome.Cancelled), (first[0].JobId, first[0].Outcome));
        Assert.Equal((1, 1), (scheduler.Pending, scheduler.Running));

        // A running job is cancelled through its token.
        scheduler.Cancel(running.Id);
        var second = await events.WaitForCompletedAsync(2);
        Assert.Equal((running.Id, JobOutcome.Cancelled), (second[1].JobId, second[1].Outcome));

        engine.Release(last.Id);
        var summary = await events.Batch.Task.WaitAsync(Timeout, Ct);
        Assert.Equal((3, 1, 2), (summary.Total, summary.Compressed, summary.Cancelled));
        Assert.DoesNotContain(queued.Id, engine.Started);

        // Unknown ids are ignored.
        scheduler.Cancel(Guid.NewGuid());
    }

    [Fact]
    public async Task Scheduler_CancelAll_ReturnsToIdleAfterRunningJobsStop()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 2), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var jobs = Enumerable.Range(0, 5).Select(i => Job($"c{i}")).ToList();
        jobs.ForEach(scheduler.Enqueue);
        scheduler.Start();
        await engine.WaitStartedAsync(2);

        scheduler.CancelAll();

        var summary = await events.Batch.Task.WaitAsync(Timeout, Ct);
        await events.WaitForStateAsync(BatchState.Idle);
        Assert.Equal((5, 5), (summary.Total, summary.Cancelled));
        Assert.Equal(2, engine.Calls);
        Assert.Equal([BatchState.Running, BatchState.Cancelling, BatchState.Idle], events.Snapshot().OfType<BatchState>());
        Assert.Equal((BatchState.Idle, 0, 0), (scheduler.State, scheduler.Pending, scheduler.Running));
    }

    [Fact]
    public async Task Scheduler_StartWhileCancelling_RunsTheNewQueueAfterwards()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 1), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var old = Job("old");
        engine.IgnoreCancellation(old.Id);
        scheduler.Enqueue(old);
        scheduler.Start();
        await engine.WaitStartedAsync(1);

        scheduler.CancelAll();
        var fresh = Job("fresh");
        scheduler.Enqueue(fresh);
        scheduler.Start();
        Assert.Equal(BatchState.Cancelling, scheduler.State);
        engine.Release(old.Id);

        await events.WaitForCompletedAsync(1);
        await engine.WaitStartedAsync(2);
        engine.Release(fresh.Id);
        await events.WaitForCompletedAsync(2);
        await events.WaitForBatchesAsync(2);

        var batches = events.Snapshot().OfType<BatchSummary>().ToList();
        Assert.Equal(1, batches[0].Total);
        Assert.Equal((1, 1), (batches[1].Total, batches[1].Compressed));
    }

    [Fact]
    public async Task Scheduler_EnqueueDuringABatch_JoinsIt()
    {
        var engine = new FakeEngine();
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 2), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var first = Job("first");
        scheduler.Enqueue(first);
        scheduler.Start();
        await engine.WaitStartedAsync(1);

        var second = Job("second");
        scheduler.Enqueue(second);
        await engine.WaitStartedAsync(2);
        engine.Release(first.Id);
        engine.Release(second.Id);

        Assert.Equal(2, (await events.Batch.Task.WaitAsync(Timeout, Ct)).Total);
        Assert.Throws<ArgumentException>(() =>
        {
            scheduler.Enqueue(second);
            scheduler.Enqueue(second);
        });
    }

    [Fact]
    public async Task Scheduler_RespectsMemoryBudget()
    {
        // Each job needs 60 % of the budget: only one fits at a time, whatever the worker count.
        var engine = new FakeEngine((job, _, _, ct) => Task.Delay(60, ct).ContinueWith(_ => Result(job, JobOutcome.Compressed, 10, 5), TaskScheduler.Default)) { Estimate = _ => 600 };
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 4), 8, 1000);
        var events = new EventLog(scheduler);
        Enumerable.Range(0, 6).Select(i => Job($"m{i}")).ToList().ForEach(scheduler.Enqueue);

        scheduler.Start();
        await events.Batch.Task.WaitAsync(Timeout, Ct);

        Assert.Equal(1, engine.MaxConcurrent);
        Assert.Equal(6, engine.Calls);
    }

    [Fact]
    public async Task Scheduler_MemoryBudget_AllowsWhatFitsAndRunsOversizedJobsAlone()
    {
        var engine = new FakeEngine((job, _, _, ct) => Task.Delay(80, ct).ContinueWith(_ => Result(job, JobOutcome.Compressed, 10, 5), TaskScheduler.Default))
        {
            Estimate = job => job.InputPath.EndsWith("huge") ? 50_000 : 300,
        };
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 8), 8, 1000);
        var events = new EventLog(scheduler);
        Enumerable.Range(0, 6).Select(i => Job($"s{i}")).Append(Job("huge")).ToList().ForEach(scheduler.Enqueue);

        scheduler.Start();
        var summary = await events.Batch.Task.WaitAsync(Timeout, Ct);

        // Three 300-byte jobs fit in 1000 bytes; the oversized one is clamped to the budget and runs alone.
        Assert.Equal(7, summary.Compressed);
        Assert.Equal(3, engine.MaxConcurrent);
        Assert.Equal(1, engine.ConcurrencyWhenStarted["huge"]);
    }

    [Fact]
    public async Task Scheduler_ThreadBudgetFollowsTheExpectedConcurrency()
    {
        var engine = new FakeEngine((job, _, _, _) => Task.FromResult(Result(job, JobOutcome.Compressed, 10, 5)));
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(), 8, 1L << 30);
        var events = new EventLog(scheduler);

        // One large job alone: every core.
        scheduler.Enqueue(Job("single"));
        scheduler.Start();
        await events.WaitForBatchesAsync(1);
        Assert.Equal(8, engine.ThreadBudgets["single"]);

        // Many jobs: one core each.
        Enumerable.Range(0, 16).Select(i => Job($"many{i}")).ToList().ForEach(scheduler.Enqueue);
        scheduler.Start();
        await events.WaitForBatchesAsync(2);
        Assert.Equal(1, engine.ThreadBudgets["many0"]);

        // Memory for two at a time: half the cores each.
        engine.Estimate = _ => 400;
        await using var tight = new BatchScheduler(engine, new EngineOptions(), 8, 1000);
        var tightEvents = new EventLog(tight);
        Enumerable.Range(0, 6).Select(i => Job($"tight{i}")).ToList().ForEach(tight.Enqueue);
        tight.Start();
        await tightEvents.WaitForBatchesAsync(1);
        Assert.Equal(4, engine.ThreadBudgets["tight0"]);
    }

    [Fact]
    public async Task Scheduler_ProgressIsThrottled()
    {
        var clock = new Stopwatch();
        var engine = new FakeEngine(async (job, progress, _, ct) =>
        {
            progress!.Report(new JobProgress(job.Id, JobStage.Probing, 0.02));
            progress.Report(new JobProgress(job.Id, JobStage.Decoding, 0.10));
            clock.Start();
            for (int i = 0; i < 400; i++)
            {
                progress.Report(new JobProgress(job.Id, JobStage.Encoding, 0.20 + (0.7 * i / 400), CandidateId: "jxl.cjxl", CandidateScore: 80 + (i / 100.0), CandidateBytes: 1000 + i));
                await Task.Delay(1, ct);
            }

            clock.Stop();
            progress.Report(new JobProgress(job.Id, JobStage.Verifying, 0.95));
            progress.Report(new JobProgress(job.Id, JobStage.Writing, 0.98));
            progress.Report(new JobProgress(job.Id, JobStage.Done, 1.0));
            return Result(job, JobOutcome.Compressed, 10, 5);
        });
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(), 4, 1L << 30);
        var events = new EventLog(scheduler);
        var job = Job("throttled");
        scheduler.Enqueue(job);

        scheduler.Start();
        await events.Batch.Task.WaitAsync(Timeout, Ct);

        var progress = events.Snapshot().OfType<JobProgress>().ToList();
        int encoding = progress.Count(p => p.Stage == JobStage.Encoding);
        int allowed = (int)(clock.Elapsed / BatchScheduler.ProgressInterval) + 2;
        Assert.InRange(encoding, 1, allowed);
        Assert.True(encoding < 400 / 4, $"{encoding} encoding events for 400 reports");

        // Stage changes are never dropped, and arrive in order, before the job's completion.
        Assert.Equal([JobStage.Probing, JobStage.Decoding, JobStage.Encoding, JobStage.Verifying, JobStage.Writing, JobStage.Done], progress.Select(p => p.Stage).Distinct());
        Assert.Equal(JobStage.Done, progress[^1].Stage);
        Assert.All(progress.Where(p => p.Stage == JobStage.Encoding), p => Assert.Equal("jxl.cjxl", p.CandidateId));
        var log = events.Snapshot();
        Assert.True(log.FindLastIndex(e => e is JobProgress) < log.FindIndex(e => e is CompressionResult));
    }

    [Fact]
    public async Task Scheduler_FaultyHandlersAndEngines_DoNotBreakTheBatch()
    {
        var engine = new FakeEngine((job, _, _, _) => job.InputPath.EndsWith("boom") ? throw new InvalidOperationException("engine bug") : Task.FromResult(Result(job, JobOutcome.Compressed, 10, 5)))
        {
            Estimate = job => job.InputPath.EndsWith("noestimate") ? throw new IOException("no header") : 1,
        };
        await using var scheduler = new BatchScheduler(engine, new EngineOptions(), 4, 1L << 30);
        var events = new EventLog(scheduler);
        scheduler.JobCompleted += (_, _) => throw new InvalidOperationException("handler bug");
        scheduler.Enqueue(Job("ok"));
        scheduler.Enqueue(Job("boom"));
        scheduler.Enqueue(Job("noestimate"));

        scheduler.Start();
        var summary = await events.Batch.Task.WaitAsync(Timeout, Ct);

        Assert.Equal((3, 2, 1), (summary.Total, summary.Compressed, summary.Failed));
        var failed = events.Snapshot().OfType<CompressionResult>().Single(r => r.Outcome == JobOutcome.Failed);
        Assert.Contains("engine bug", failed.Error);
    }

    [Fact]
    public async Task Scheduler_DisposeAsync_CancelsAndReturnsQuickly()
    {
        var engine = new FakeEngine();
        var scheduler = new BatchScheduler(engine, new EngineOptions(MaxConcurrency: 2), 4, 1L << 30);
        var events = new EventLog(scheduler);
        Enumerable.Range(0, 4).Select(i => Job($"d{i}")).ToList().ForEach(scheduler.Enqueue);
        scheduler.Start();
        await engine.WaitStartedAsync(2);

        var clock = Stopwatch.StartNew();
        await scheduler.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.5), clock.Elapsed.ToString());
        Assert.Equal(0, engine.Concurrent);
        Assert.Equal(4, events.Snapshot().OfType<CompressionResult>().Count(r => r.Outcome == JobOutcome.Cancelled));
        Assert.Throws<ObjectDisposedException>(() => scheduler.Enqueue(Job("late")));
        scheduler.Start();
        scheduler.CancelAll();
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task Scheduler_CancelMidJob_LeavesNoOutputAndNoProcess()
    {
        RequireTools();
        using var dir = new TempDir("scheduler-cancel");
        string input = PhotoJpeg(dir, "large.jpg", 800);
        using var ctx = Context(dir);
        var options = new EngineOptions(MaxConcurrency: 1);
        await using var scheduler = new BatchScheduler(new CompressionEngine(ctx), options);
        var events = new EventLog(scheduler);
        var seenProcesses = new ConcurrentDictionary<int, bool>();
        var encoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Progress += (_, p) =>
        {
            if (p.Stage == JobStage.Encoding)
            {
                encoding.TrySetResult();
            }
        };
        var job = CompressionJob.Create(input, BuiltInProfiles.Vs with { Effort = EffortLevel.Insane }, new OutputPlan());
        scheduler.Enqueue(job);

        scheduler.Start();
        await encoding.Task.WaitAsync(Timeout, Ct);

        // Let the encoders get going, remember every tool process, then cancel in the middle of the work.
        Assert.True(ProcessTree.WaitUntil(() =>
        {
            foreach (int pid in ctx.Runner.ActiveProcessIds)
            {
                seenProcesses.TryAdd(pid, true);
            }

            return seenProcesses.Count >= 2;
        }, TimeSpan.FromSeconds(10)));
        scheduler.Cancel(job.Id);
        var result = (await events.WaitForCompletedAsync(1))[0];

        Assert.Equal(JobOutcome.Cancelled, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal(0, ctx.Runner.ActiveCount);
        Assert.All(seenProcesses.Keys, pid => Assert.False(ProcessTree.IsAlive(pid), $"tool process {pid} survived"));
        Assert.Equal(["large.jpg"], UserFiles(dir));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "Carbonado")));
        Assert.Empty(Directory.EnumerateDirectories(ctx.SessionTemp, "job-*"));
        Assert.Equal(1, (await events.Batch.Task.WaitAsync(Timeout, Ct)).Cancelled);
    }

    private static CompressionJob Job(string name) =>
        CompressionJob.Create(@"C:\fake\" + name, BuiltInProfiles.Vs, new OutputPlan());

    private static CompressionResult Result(CompressionJob job, JobOutcome outcome, long original, long output) =>
        new(job.Id, job.InputPath, outcome == JobOutcome.Compressed ? job.InputPath + ".avif" : null, outcome, original, output,
            OutputFormat.Avif, "avif.avifenc", 86, VerificationKind.PerceptualScore, TimeSpan.FromMilliseconds(5), [], null, 10, 10);

    /// <summary>
    /// A scriptable engine. By default each job blocks until <see cref="Release"/>d (then Compressed) or cancelled
    /// (then Cancelled); a custom body replaces that.
    /// </summary>
    private sealed class FakeEngine : IJobEngine
    {
        private readonly Func<CompressionJob, IProgress<JobProgress>?, int, CancellationToken, Task<CompressionResult>>? _body;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<Guid, bool> _ignoreCancel = new();
        private readonly SemaphoreSlim _started = new(0);
        private int _calls;
        private int _concurrent;
        private int _maxConcurrent;

        public FakeEngine(Func<CompressionJob, IProgress<JobProgress>?, int, CancellationToken, Task<CompressionResult>>? body = null) => _body = body;

        public Func<CompressionJob, long> Estimate { get; set; } = _ => 1;

        public int Calls => Volatile.Read(ref _calls);

        public int Concurrent => Volatile.Read(ref _concurrent);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public ConcurrentBag<Guid> Started { get; } = [];

        public ConcurrentDictionary<string, int> ThreadBudgets { get; } = new();

        public ConcurrentDictionary<string, int> ConcurrencyWhenStarted { get; } = new();

        public long EstimateMemoryBytes(CompressionJob job) => Estimate(job);

        public async Task<CompressionResult> RunAsync(CompressionJob job, IProgress<JobProgress>? progress, int threadBudget, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref _concurrent);
            int max;
            while (now > (max = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, max) != max)
            {
            }

            Interlocked.Increment(ref _calls);
            string name = Path.GetFileName(job.InputPath);
            ThreadBudgets[name] = threadBudget;
            ConcurrencyWhenStarted[name] = now;
            Started.Add(job.Id);
            _started.Release();
            try
            {
                if (_body is not null)
                {
                    return await _body(job, progress, threadBudget, ct);
                }

                var gate = _gates.GetOrAdd(job.Id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                try
                {
                    await gate.Task.WaitAsync(_ignoreCancel.ContainsKey(job.Id) ? CancellationToken.None : ct);
                }
                catch (OperationCanceledException)
                {
                    return CompressionResult.Cancel(job);
                }

                return ct.IsCancellationRequested ? CompressionResult.Cancel(job) : Result(job, JobOutcome.Compressed, 100, 40);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public void Release(Guid id) => _gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        /// <summary>The job keeps running after cancellation until released (a slow-to-stop encoder).</summary>
        public void IgnoreCancellation(Guid id) => _ignoreCancel[id] = true;

        public async Task WaitStartedAsync(int count)
        {
            var clock = Stopwatch.StartNew();
            while (Calls < count)
            {
                Assert.True(clock.Elapsed < Timeout, $"only {Calls} of {count} jobs started");
                await _started.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);
            }
        }
    }

    /// <summary>Records every scheduler event in delivery order.</summary>
    private sealed class EventLog
    {
        private readonly List<object> _events = [];
        private readonly BatchScheduler _scheduler;

        public EventLog(BatchScheduler scheduler)
        {
            _scheduler = scheduler;
            scheduler.Progress += (_, e) => Add(e);
            scheduler.JobCompleted += (_, e) => Add(e);
            scheduler.StateChanged += (_, e) => Add(e);
            scheduler.BatchCompleted += (_, e) =>
            {
                Add(e);
                Batch.TrySetResult(e);
            };
        }

        public TaskCompletionSource<BatchSummary> Batch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<object> Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public async Task<List<CompressionResult>> WaitForCompletedAsync(int count)
        {
            await WaitAsync(() => Snapshot().OfType<CompressionResult>().Count() >= count, $"{count} completed jobs");
            return Snapshot().OfType<CompressionResult>().ToList();
        }

        public Task WaitForBatchesAsync(int count) => WaitAsync(() => Snapshot().OfType<BatchSummary>().Count() >= count, $"{count} batches");

        public Task WaitForStateAsync(BatchState state) => WaitAsync(() => _scheduler.State == state && Snapshot().OfType<BatchState>().LastOrDefault() == state, $"state {state}");

        private static async Task WaitAsync(Func<bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < Timeout, "timed out waiting for " + what);
                await Task.Delay(10, Ct);
            }
        }

        private void Add(object e)
        {
            lock (_events)
            {
                _events.Add(e);
            }
        }
    }
}
