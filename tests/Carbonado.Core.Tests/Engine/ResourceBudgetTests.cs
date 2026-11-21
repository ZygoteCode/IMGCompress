using Carbonado.Core.Engine;

namespace Carbonado.Core.Tests.Engine;

public class ResourceBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Budget_GrantsWhatFitsAndReleases()
    {
        var budget = new ResourceBudget(threads: 4, memoryBytes: 1000);

        using (var a = await budget.AcquireAsync(1, 600, Ct))
        {
            Assert.Equal((3, 400L), (budget.AvailableThreads, budget.AvailableBytes));
            var waiting = budget.AcquireAsync(1, 600, Ct);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, budget.Waiting);
            a.Dispose();
            using var b = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal((3, 400L), (budget.AvailableThreads, budget.AvailableBytes));
            a.Dispose(); // releasing twice is harmless
            Assert.Equal((3, 400L), (budget.AvailableThreads, budget.AvailableBytes));
        }

        Assert.Equal((4, 1000L), (budget.AvailableThreads, budget.AvailableBytes));
    }

    [Fact]
    public async Task Budget_ThreadSlotsLimitConcurrency()
    {
        var budget = new ResourceBudget(threads: 2, memoryBytes: 1L << 40);
        using var a = await budget.AcquireAsync(1, 1, Ct);
        using var b = await budget.AcquireAsync(1, 1, Ct);

        var third = budget.AcquireAsync(1, 1, Ct);

        Assert.False(third.IsCompleted);
        b.Dispose();
        using var c = await third.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, budget.AvailableThreads);
    }

    [Fact]
    public async Task Budget_IsStrictlyFirstInFirstOut()
    {
        var budget = new ResourceBudget(threads: 8, memoryBytes: 1000);
        var holder = await budget.AcquireAsync(1, 900, Ct);
        var big = budget.AcquireAsync(1, 800, Ct);
        var small = budget.AcquireAsync(1, 50, Ct);

        // 100 bytes are free: the small request fits but must not overtake the big one ahead of it.
        await Task.Delay(50, Ct);
        Assert.False(big.IsCompleted);
        Assert.False(small.IsCompleted);

        holder.Dispose();
        using var bigLease = await big.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        using var smallLease = await small.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(150, budget.AvailableBytes);
    }

    [Fact]
    public async Task Budget_ClampsRequestsLargerThanTheTotal()
    {
        var budget = new ResourceBudget(threads: 2, memoryBytes: 1000);

        using var lease = await budget.AcquireAsync(5, 1_000_000, Ct);

        Assert.Equal((2, 1000L), (lease.Threads, lease.Bytes));
        Assert.Equal((0, 0L), (budget.AvailableThreads, budget.AvailableBytes));
    }

    [Fact]
    public async Task Budget_CancelledWaiterLeavesTheQueueAndUnblocksTheNext()
    {
        var budget = new ResourceBudget(threads: 4, memoryBytes: 1000);
        using var holder = await budget.AcquireAsync(1, 500, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var blocked = budget.AcquireAsync(1, 900, cts.Token);
        var behind = budget.AcquireAsync(1, 400, Ct);
        Assert.False(behind.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.AsTask());
        using var next = await behind.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(100, budget.AvailableBytes);
        Assert.Equal(0, budget.Waiting);
    }

    [Fact]
    public async Task Budget_AlreadyCancelledTokenThrowsWithoutTakingAnything()
    {
        var budget = new ResourceBudget(threads: 1, memoryBytes: 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.AcquireAsync(1, 1, cts.Token).AsTask());
        Assert.Equal((1, 10L), (budget.AvailableThreads, budget.AvailableBytes));
    }

    [Fact]
    public async Task Budget_ManyConcurrentUsersNeverExceedIt()
    {
        var budget = new ResourceBudget(threads: 3, memoryBytes: 1000);
        int inside = 0, peakThreads = 0;
        long bytesInside = 0, peakBytes = 0;
        var gate = new Lock();

        await Parallel.ForAsync(0, 200, Ct, async (i, ct) =>
        {
            using var lease = await budget.AcquireAsync(1, 100 + (i % 5 * 100), ct);
            lock (gate)
            {
                inside++;
                bytesInside += lease.Bytes;
                peakThreads = Math.Max(peakThreads, inside);
                peakBytes = Math.Max(peakBytes, bytesInside);
            }

            await Task.Yield();
            lock (gate)
            {
                inside--;
                bytesInside -= lease.Bytes;
            }
        });

        Assert.InRange(peakThreads, 1, 3);
        Assert.InRange(peakBytes, 100, 1000);
        Assert.Equal((3, 1000L), (budget.AvailableThreads, budget.AvailableBytes));
    }

    [Fact]
    public void Budget_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceBudget(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceBudget(1, 0));
        var budget = new ResourceBudget(1, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AcquireAsync(-1, 1, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AcquireAsync(1, -1, Ct));
    }
}
