using System.Diagnostics;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Tools;

public class ProcessRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runner_CapturesExitCodeAndStderr()
    {
        string cjxl = TestTools.Require(ToolId.Cjxl);
        using var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessSpec(cjxl, ["--no-such-flag", "in.png", "out.jxl"]), Ct);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Contains("--no-such-flag", result.Stderr);
        Assert.Empty(result.Stdout);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.Equal(0, runner.ActiveCount);

        var ok = await runner.RunAsync(new ProcessSpec(cjxl, ["--version"], CaptureStdout: true), Ct);
        Assert.True(ok.Success);
        Assert.Contains("0.13", System.Text.Encoding.ASCII.GetString(ok.Stdout) + ok.Stderr);
    }

    [Fact]
    public async Task Runner_StdoutBinary()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var dir = new TempDir("runner");
        using var px = TestImages.WithAlpha(37, 23);
        File.WriteAllBytes(dir.File("in.png"), PngTestFiles.Still(px));
        using var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            new ProcessSpec(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", "in.png", "-f", "rawvideo", "-pix_fmt", "rgba", "-"], dir.Path, CaptureStdout: true),
            Ct);
        Assert.True(result.Success, result.Stderr);
        Assert.Equal(px.ByteLength, result.Stdout.Length);
        Assert.True(px.AsSpan().SequenceEqual(result.Stdout));
    }

    [Fact]
    public async Task Runner_WritesStdin()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var px = TestImages.PhotoLike(20, 10);
        using var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            new ProcessSpec(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "png_pipe", "-i", "-", "-f", "rawvideo", "-pix_fmt", "rgba", "-"], CaptureStdout: true, Stdin: PngTestFiles.Still(px)),
            Ct);
        Assert.True(result.Success, result.Stderr);
        Assert.True(px.AsSpan().SequenceEqual(result.Stdout));
    }

    [Fact]
    public async Task Runner_Timeout_KillsProcess()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var runner = new ProcessRunner();
        var sw = Stopwatch.StartNew();
        var run = runner.RunAsync(new ProcessSpec(ffmpeg, EndlessFfmpeg, Timeout: TimeSpan.FromMilliseconds(800)), Ct);
        int pid = await WaitForSingleActiveProcess(runner);
        var result = await run;
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
        Assert.False(ProcessTree.IsAlive(pid));
        Assert.Equal(0, runner.ActiveCount);
    }

    [Fact]
    public async Task Runner_Cancel_KillsProcess()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var runner = new ProcessRunner();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(new ProcessSpec(ffmpeg, EndlessFfmpeg), cts.Token);
        int pid = await WaitForSingleActiveProcess(runner);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(ProcessTree.IsAlive(pid));
        Assert.Equal(0, runner.ActiveCount);

        // A token cancelled up front never starts a process.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new ProcessSpec(ffmpeg, ["-version"]), cts.Token));
    }

    [Fact]
    public async Task Runner_DisposeKillsChildren()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var runner = new ProcessRunner();

        // cmd.exe → ping.exe: the grandchild must die with the job too.
        var run = runner.RunAsync(new ProcessSpec(cmd, ["/c", "ping", "-n", "60", "127.0.0.1"]), Ct);
        int pid = await WaitForSingleActiveProcess(runner);
        List<int> grandchildren = [];
        Assert.True(ProcessTree.WaitUntil(() => (grandchildren = ProcessTree.Children(pid)).Count > 0, TimeSpan.FromSeconds(10)));

        runner.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(ProcessTree.WaitUntil(() => !ProcessTree.IsAlive(pid), TimeSpan.FromSeconds(5)), "child alive");
        foreach (int g in grandchildren)
        {
            Assert.True(ProcessTree.WaitUntil(() => !ProcessTree.IsAlive(g), TimeSpan.FromSeconds(5)), "grandchild alive");
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.RunAsync(new ProcessSpec(cmd, ["/c", "exit"]), Ct));
    }

    [Fact]
    public async Task Runner_KillsDescendantsThatKeepThePipesOpen()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var runner = new ProcessRunner();

        // cmd exits at once; the ping it starts in the background inherits stdout/stderr and keeps them open.
        var run = runner.RunAsync(new ProcessSpec(cmd, ["/c", "start", "/b", "ping", "-n", "120", "127.0.0.1"], Timeout: TimeSpan.FromSeconds(1)), Ct);
        int pid = await WaitForSingleActiveProcess(runner);
        List<int> holders = [];
        Assert.True(ProcessTree.WaitUntil(() => (holders = ProcessTree.Children(pid)).Count > 0, TimeSpan.FromSeconds(10)));

        // The run ends after the timeout and the grace period, and the pipe holders do not outlive it.
        var clock = Stopwatch.StartNew();
        await run;
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
        foreach (int holder in holders)
        {
            Assert.True(ProcessTree.WaitUntil(() => !ProcessTree.IsAlive(holder), TimeSpan.FromSeconds(5)), "a descendant holding the pipes is still alive");
        }
    }

    [Fact]
    public async Task Runner_KillAll_StopsRunningProcessesAndKeepsWorking()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var runner = new ProcessRunner();
        var runs = Enumerable.Range(0, 3).Select(_ => runner.RunAsync(new ProcessSpec(ffmpeg, EndlessFfmpeg), Ct)).ToArray();
        Assert.True(ProcessTree.WaitUntil(() => runner.ActiveCount == 3, TimeSpan.FromSeconds(10)));
        int[] pids = [.. runner.ActiveProcessIds];
        runner.KillAll();
        foreach (var run in runs)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        Assert.All(pids, p => Assert.False(ProcessTree.IsAlive(p)));
        var after = await runner.RunAsync(new ProcessSpec(ffmpeg, ["-hide_banner", "-version"], CaptureStdout: true), Ct);
        Assert.True(after.Success);
    }

    [Fact]
    public async Task Runner_PassesUnicodePathsVerbatim()
    {
        string cwebp = TestTools.Require(ToolId.Cwebp);
        string dwebp = TestTools.Require(ToolId.Dwebp);
        using var dir = new TempDir("runner");
        string folder = dir.Sub("àèìòù 日本 x y");
        string input = Path.Combine(folder, "àèìòù 日本 x y.png");
        string webp = Path.Combine(folder, "àèìòù 日本 x y.webp");
        string pam = Path.Combine(folder, "àèìòù 日本 x y.pam");
        using var px = TestImages.Graphic(40, 30);
        File.WriteAllBytes(input, PngTestFiles.Still(px));
        using var runner = new ProcessRunner();

        var encode = await runner.RunAsync(new ProcessSpec(cwebp, ["-quiet", "-lossless", "-exact", input, "-o", webp]), Ct);
        Assert.True(encode.Success, encode.Stderr);
        var decode = await runner.RunAsync(new ProcessSpec(dwebp, ["-quiet", webp, "-pam", "-o", pam]), Ct);
        Assert.True(decode.Success, decode.Stderr);
        using var roundTrip = PngTestFiles.DecodePamFile(pam);
        PixelAssert.Identical(px, roundTrip.Pixels);
    }

    [Fact]
    public async Task Runner_CapsStderr()
    {
        string ffmpeg = TestTools.Require(ToolId.Ffmpeg);
        using var runner = new ProcessRunner();
        // showinfo logs one line per frame: ~180 KB of stderr.
        var result = await runner.RunAsync(new ProcessSpec(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "info", "-f", "lavfi", "-i", "testsrc=d=20:s=32x32:r=25,showinfo", "-f", "null", "-"]), Ct);
        Assert.True(result.Success);
        Assert.InRange(result.Stderr.Length, ProcessRunner.MaxStderrChars - 10, ProcessRunner.MaxStderrChars + 10);
        Assert.Contains("\n…\n", result.Stderr);
        Assert.Contains("Input #0", result.Stderr[..result.Stderr.IndexOf("\n…\n", StringComparison.Ordinal)]);

        // The tail (end of the log) survives the cap.
        Assert.Contains("n: 499", result.Stderr);
    }

    [Fact]
    public async Task Runner_MissingExecutable_Throws()
    {
        using var runner = new ProcessRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new ProcessSpec(@"C:\no\such\tool.exe", []), Ct));
        Assert.Equal(0, runner.ActiveCount);
    }

    [Fact]
    public void Runner_SharedIsSingleton()
    {
        Assert.Same(ProcessRunner.Shared, ProcessRunner.Shared);
        Assert.True(new ProcessResult(0, [], string.Empty, TimeSpan.Zero, false).Success);
        Assert.False(new ProcessResult(0, [], string.Empty, TimeSpan.Zero, true).Success);
        Assert.False(new ProcessResult(2, [], string.Empty, TimeSpan.Zero, false).Success);
        Assert.Equal(ProcessPriorityClass.BelowNormal, new ProcessSpec("x", []).Priority);
    }

    private static readonly string[] EndlessFfmpeg =
        ["-hide_banner", "-nostdin", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "nullsrc=s=16x16:r=25", "-f", "null", "-"];

    private static async Task<int> WaitForSingleActiveProcess(ProcessRunner runner)
    {
        var sw = Stopwatch.StartNew();
        while (runner.ActiveCount == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, Ct);
        }

        return Assert.Single(runner.ActiveProcessIds);
    }
}
