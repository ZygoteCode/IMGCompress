using System.Diagnostics;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>
/// What a piece of work allocated: managed bytes (process-wide, exact) and the growth of the peak commit charge
/// (private bytes, which native pixel buffers count towards even untouched). Only meaningful in the
/// <see cref="MemorySensitive"/> collection, where no other test runs concurrently.
/// </summary>
internal readonly record struct Allocations(long ManagedBytes, long PeakCommitGrowth)
{
    public const long ManagedLimit = 32L << 20;

    public const long CommitLimit = 256L << 20;

    public static Allocations Measure(Action action)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long peak = self.PeakPagedMemorySize64;
        long managed = GC.GetTotalAllocatedBytes(precise: true);
        action();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - managed;
        self.Refresh();
        return new Allocations(allocated, self.PeakPagedMemorySize64 - peak);
    }

    public static async Task<Allocations> MeasureAsync(Func<Task> action)
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long peak = self.PeakPagedMemorySize64;
        long managed = GC.GetTotalAllocatedBytes(precise: true);
        await action();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - managed;
        self.Refresh();
        return new Allocations(allocated, self.PeakPagedMemorySize64 - peak);
    }

    public void AssertSmall()
    {
        Assert.True(ManagedBytes < ManagedLimit, $"{ManagedBytes >> 20} MiB of managed memory allocated");
        Assert.True(PeakCommitGrowth < CommitLimit, $"peak commit grew by {PeakCommitGrowth >> 20} MiB");
    }
}
