using Carbonado.Core.Engine;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Engine;

public class EngineContextTests
{
    [Fact]
    public async Task Context_CreatesAPrivateSessionAndCleansItUpOnDispose()
    {
        using var dir = new TempDir("context");
        string root = dir.Sub("temp-root");

        var ctx = EngineContext.Create(FormatFixtures.Locator, new EngineOptions(TempRoot: root));
        string session = ctx.SessionTemp;
        Assert.True(Directory.Exists(session));
        Assert.Equal(root, Path.GetDirectoryName(session));
        Assert.NotSame(ProcessRunner.Shared, ctx.Runner);
        Assert.NotEmpty(ctx.Encoders.Available);
        Assert.Same(ctx.Loader, ctx.Verifier.Loader);
        File.WriteAllText(Path.Combine(session, "leftover.tmp"), "x");

        ctx.Dispose();
        ctx.Dispose();

        Assert.False(Directory.Exists(session));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ctx.Runner.RunAsync(new ProcessSpec("cmd.exe", ["/c", "exit"]), TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => ctx.CreateJobFolder(Guid.NewGuid()));
    }

    [Fact]
    public async Task Context_WiresUpLikeTheApp()
    {
        // The composition used by the app (EngineBootstrap): context → engine → scheduler behind IBatchProcessor.
        using var dir = new TempDir("context-app");
        var options = new EngineOptions(MaxConcurrency: null, TempRoot: dir.Sub("session"));
        var ctx = EngineContext.Create(FormatFixtures.Locator, options);
        IBatchProcessor processor = new BatchScheduler(new CompressionEngine(ctx), options);

        Assert.Equal(BatchState.Idle, processor.State);
        processor.Start();
        processor.CancelAll();

        // App shutdown: cancel, then dispose the context without waiting for anything.
        ctx.Dispose();
        await processor.DisposeAsync();
        Assert.Equal(BatchState.Idle, processor.State);
    }

    [Fact]
    public async Task Context_OutOfRangeOptionsFallBackToDefaults()
    {
        using var dir = new TempDir("context-options");

        using var ctx = EngineContext.Create(FormatFixtures.Locator, new EngineOptions(MaxConcurrency: 0, MemoryBudgetFraction: 7, ScoreTolerance: -1, TempRoot: dir.Path));
        await using var scheduler = new BatchScheduler(new CompressionEngine(ctx), ctx.Options);

        // MaxConcurrency 0 means one worker per core; a fraction outside (0, 1] means a quarter of the memory.
        Assert.Equal(Environment.ProcessorCount, scheduler.Budget.TotalThreads);
        long physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Assert.Equal(Math.Max(256L << 20, (long)(physical * 0.25)), scheduler.Budget.TotalBytes);
    }
}
