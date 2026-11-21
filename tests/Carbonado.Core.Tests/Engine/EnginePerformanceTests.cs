using System.Diagnostics;
using Carbonado.Core.Engine;
using Carbonado.Core.Tests.TestSupport;
using static Carbonado.Core.Tests.Engine.EngineTestSupport;

namespace Carbonado.Core.Tests.Engine;

public class EnginePerformanceTests
{
    // The calibration image of the design spec (§1): 3840×2400, 1.6 MB JPEG.
    private const string CalibrationPhoto = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Engine_LargeSystemPhoto_DefaultProfile()
    {
#if DEBUG
        Assert.Skip("Performance is measured in Release builds only.");
#endif
        RequireTools();
        if (!File.Exists(CalibrationPhoto))
        {
            Assert.Skip("The 3840×2400 calibration wallpaper is not installed.");
        }

        using var dir = new TempDir("engine-perf");
        string input = dir.File("img0.jpg");
        File.Copy(CalibrationPhoto, input);
        using var ctx = Context(dir);
        var progress = new List<JobProgress>();

        var clock = Stopwatch.StartNew();
        var result = await RunAsync(ctx, input, BuiltInProfiles.Default, progress: progress);
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"{Environment.ProcessorCount} logical cores; wall clock {clock.Elapsed.TotalSeconds:0.00} s; {progress.Count} progress reports");
        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal((3840, 2400), (result.Width, result.Height));
        Assert.Equal(VerificationKind.PerceptualScore, result.Verification);
        Assert.True(result.Score >= 85);
        Assert.True(result.OutputBytes < result.OriginalBytes / 4, $"{result.OutputBytes} B");
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), $"{clock.Elapsed.TotalSeconds:0.0} s");
    }
}
