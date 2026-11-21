using Carbonado.App.Services;
using Carbonado.App.Tests.TestSupport;
using Carbonado.Core.Engine;

namespace Carbonado.App.Tests.Services;

public class DeferredBatchProcessorTests
{
    private static CompressionJob Job(string name) =>
        CompressionJob.Create(name, BuiltInProfiles.Default, new OutputPlan());

    [Fact]
    public void CallsBeforeAttach_AreReplayedInOrder_AndEventsForwarded()
    {
        var proxy = new DeferredBatchProcessor();
        var a = Job("a.png");
        var b = Job("b.png");
        proxy.Enqueue(a);
        proxy.Enqueue(b);
        proxy.Start();
        Assert.Equal(2, proxy.Pending);
        Assert.Equal(BatchState.Running, proxy.State);

        var real = new FakeBatch();
        var completed = new List<CompressionResult>();
        proxy.JobCompleted += (_, r) => completed.Add(r);
        proxy.Attach(real);

        Assert.Equal([a, b], real.Enqueued);
        Assert.Equal(1, real.Starts);
        real.RaiseCompleted(CompressionResult.Cancel(a));
        Assert.Single(completed);

        var c = Job("c.png");
        proxy.Enqueue(c);
        Assert.Equal(3, real.Enqueued.Count);
    }

    [Fact]
    public void CancelBeforeAttach_DropsJobAndReportsCancelled()
    {
        var proxy = new DeferredBatchProcessor();
        var a = Job("a.png");
        var b = Job("b.png");
        proxy.Enqueue(a);
        proxy.Enqueue(b);
        var completed = new List<CompressionResult>();
        proxy.JobCompleted += (_, r) => completed.Add(r);

        proxy.Cancel(a.Id);
        Assert.Equal(JobOutcome.Cancelled, Assert.Single(completed).Outcome);

        var real = new FakeBatch();
        proxy.Attach(real);
        Assert.Equal([b], real.Enqueued);
    }

    [Fact]
    public void CancelAllBeforeAttach_ReportsEveryJobAndBatchCompleted()
    {
        var proxy = new DeferredBatchProcessor();
        proxy.Enqueue(Job("a.png"));
        proxy.Enqueue(Job("b.png"));
        proxy.Start();
        int cancelled = 0;
        BatchSummary? summary = null;
        proxy.JobCompleted += (_, _) => cancelled++;
        proxy.BatchCompleted += (_, s) => summary = s;

        proxy.CancelAll();

        Assert.Equal(2, cancelled);
        Assert.Equal(2, summary!.Cancelled);
        var real = new FakeBatch();
        proxy.Attach(real);
        Assert.Empty(real.Enqueued);
        Assert.Equal(0, real.Starts);
    }
}
