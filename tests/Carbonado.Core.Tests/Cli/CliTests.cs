using System.Text.Json;
using Carbonado.Cli;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Engine;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Cli;

public class CliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Cli_ParsesAllOptions()
    {
        var options = CliOptions.Parse(["a.png", "-o", "out", "photos", "-p", "web", "-f", "avif", "-q", "88.5", "-r", "--effort", "max", "--json", "--dry-run", "--data-dir", "data", "--", "-dash.png"]);

        Assert.Equal(["a.png", "photos", "-dash.png"], options.Paths);
        Assert.Equal("out", options.OutputDir);
        Assert.Equal("web", options.ProfileId);
        Assert.Equal(OutputFormat.Avif, options.Format);
        Assert.Equal(88.5, options.Quality);
        Assert.True(options.Recursive);
        Assert.Equal(EffortLevel.Max, options.Effort);
        Assert.True(options.Json);
        Assert.True(options.DryRun);
        Assert.Equal("data", options.DataDir);
        Assert.False(options.Lossless);
        Assert.False(options.Help);

        var inline = CliOptions.Parse(["x.jpg", "--output=o", "--profile=KEEP", "--format=jpg", "--quality=70", "--recursive", "--effort=insane"]);
        Assert.Equal(("o", "KEEP", OutputFormat.Jpeg, 70.0, true, EffortLevel.Insane), (inline.OutputDir, inline.ProfileId, inline.Format, inline.Quality, inline.Recursive, inline.Effort));

        var auto = CliOptions.Parse(["x.jpg", "-f", "png", "-f", "auto", "--lossless"]);
        Assert.Null(auto.Format);
        Assert.True(auto.AutoFormat);
        Assert.True(auto.Lossless);

        Assert.True(CliOptions.Parse(["--help"]).Help);
        Assert.True(CliOptions.Parse(["-h"]).Help);
    }

    [Fact]
    public void Cli_BuildsProfilesAndPlans()
    {
        var fixedFormat = CliOptions.Parse(["x.png", "-f", "jxl", "-q", "92", "--effort", "fast"]).BuildProfile();
        Assert.Equal((FormatStrategy.Fixed, QualityMode.TargetScore, 92.0, EffortLevel.Fast), (fixedFormat.Strategy, fixedFormat.Mode, fixedFormat.TargetScore, fixedFormat.Effort));
        Assert.Equal([OutputFormat.Jxl], fixedFormat.Formats);
        Assert.Equal("vs", fixedFormat.Id);

        var lossless = CliOptions.Parse(["x.png", "--lossless"]).BuildProfile();
        Assert.Equal(QualityMode.Lossless, lossless.Mode);
        Assert.Equal(BuiltInProfiles.Vs.Formats, lossless.Formats);

        var keepAuto = CliOptions.Parse(["x.png", "-p", "keep", "-f", "auto"]).BuildProfile();
        Assert.Equal(FormatStrategy.Auto, keepAuto.Strategy);

        Assert.Equal(BuiltInProfiles.Email, CliOptions.Parse(["x.png", "-p", "email"]).BuildProfile());

        Assert.Equal(new OutputPlan(), CliOptions.Parse(["x.png"]).BuildOutputPlan());
        var custom = CliOptions.Parse(["x.png", "-o", "out"]).BuildOutputPlan();
        Assert.Equal((OutputLocation.CustomFolder, Path.GetFullPath("out"), true), (custom.Location, custom.CustomFolder, custom.MirrorTree));
    }

    [Theory]
    [InlineData(new string[0], "No images given")]
    [InlineData(new[] { "a.png", "--bogus" }, "Unknown option --bogus")]
    [InlineData(new[] { "a.png", "-o" }, "needs a value")]
    [InlineData(new[] { "a.png", "-f", "bmp" }, "Unknown format")]
    [InlineData(new[] { "a.png", "-q", "abc" }, "not a number")]
    [InlineData(new[] { "a.png", "-q", "150" }, "between 30 and 99")]
    [InlineData(new[] { "a.png", "-p", "nope" }, "Unknown profile")]
    [InlineData(new[] { "a.png", "--effort", "ludicrous" }, "Unknown effort")]
    [InlineData(new[] { "a.png", "--lossless", "-q", "90" }, "cannot be combined")]
    [InlineData(new[] { "a.png", "--json=yes" }, "takes no value")]
    public async Task Cli_UsageErrorExit2(string[] args, string message)
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(args));
        var (code, stdout, stderr) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains(message, stderr);
        Assert.Contains("--help", stderr);
    }

    [Fact]
    public async Task Cli_HelpExit0()
    {
        var (code, stdout, _) = await RunAsync(["--help"]);

        Assert.Equal(0, code);
        Assert.Contains("Usage: carbonado <paths…>", stdout);
        Assert.Contains("--dry-run", stdout);
    }

    [Fact]
    public void Cli_FormatsResultLines()
    {
        var job = CompressionJob.Create(@"C:\p\photo.jpg", BuiltInProfiles.Vs, new OutputPlan());
        var ok = new CompressionResult(job.Id, job.InputPath, @"C:\p\Carbonado\photo.avif", JobOutcome.Compressed, 1_258_291, 146_432, OutputFormat.Avif, "avif.avifenc", 86.94,
            VerificationKind.PerceptualScore, TimeSpan.FromSeconds(3), [], null, 3840, 2400);

        Assert.Equal("ok  photo.jpg → photo.avif  1.20 MB → 143 KB  −88%  VS 86.9", CliRunner.FormatLine(ok));
        Assert.Equal("FL bit-exact", CliRunner.Quality(ok with { Verification = VerificationKind.BitExactReconstruction, Score = null }));
        Assert.Equal("FL pixel-exact", CliRunner.Quality(ok with { Verification = VerificationKind.PixelExact, Score = null }));
        Assert.Equal("VVS 91.0", CliRunner.Quality(ok with { Score = 91 }));
        Assert.Equal("52.0", CliRunner.Quality(ok with { Score = 52 }));
        Assert.Equal("fail  photo.jpg  The file is empty.", CliRunner.FormatLine(CompressionResult.Fail(job, "The file is empty.")));
        Assert.StartsWith("kept  photo.jpg  1.20 MB  ", CliRunner.FormatLine(ok with { Outcome = JobOutcome.KeptOriginal, OutputBytes = 1_258_291, Error = "note" }));
        Assert.Equal("cancelled  photo.jpg", CliRunner.FormatLine(CompressionResult.Cancel(job)));

        Assert.Equal(["512 B", "1.00 KB", "9.77 KB", "97.7 KB", "977 KB", "1.20 MB", "12.0 GB"],
            new long[] { 512, 1024, 10_000, 100_000, 1_000_000, 1_258_291, 12L << 30 }.Select(CliRunner.FormatBytes));
    }

    [Fact]
    public async Task Cli_EndToEnd_CompressesFolderJson()
    {
        EngineTestSupport.RequireTools();
        using var dir = new TempDir("cli-e2e");
        string photos = dir.Sub("photos");
        string nested = Directory.CreateDirectory(Path.Combine(photos, "nested")).FullName;
        using (var graphic = TestImages.Graphic(160, 100))
        using (var photo = TestImages.PhotoLike(160, 120))
        {
            TestImages.SavePngWithWic(graphic, Path.Combine(photos, "ui.png"));
            EngineTestSupport.WritePng(Path.Combine(nested, "view.png"), photo);
        }

        File.WriteAllText(Path.Combine(photos, "notes.txt"), "not an image");
        string data = dir.Sub("data");

        var (code, stdout, stderr) = await RunAsync([photos, "-r", "--json", "--effort", "fast", "--data-dir", data]);

        Assert.True(code == 0, stderr + stdout);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        var results = lines.Select(l => JsonElement.Parse(l)).ToDictionary(e => Path.GetFileName(e.GetProperty("input").GetString())!);
        Assert.Equal(["ui.png", "view.png"], results.Keys.Order());
        foreach (var (name, r) in results)
        {
            Assert.Equal("compressed", r.GetProperty("outcome").GetString());
            string output = r.GetProperty("output").GetString()!;
            Assert.True(File.Exists(output), output);
            Assert.Equal(new FileInfo(output).Length, r.GetProperty("outputBytes").GetInt64());
            Assert.True(r.GetProperty("outputBytes").GetInt64() < r.GetProperty("originalBytes").GetInt64());
            Assert.Contains(r.GetProperty("verification").GetString(), new[] { "perceptualScore", "pixelExact" });
            Assert.Equal(Path.Combine(Path.GetDirectoryName(r.GetProperty("input").GetString())!, "Carbonado"), Path.GetDirectoryName(output));
            Assert.Equal(JsonValueKind.Null, r.GetProperty("note").ValueKind);
        }

        // The temp session of the run is gone; only logs remain in the data folder.
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "temp")));

        // A second run does not pick up the outputs (Carbonado folders are skipped); a broken file fails alone with exit code 1.
        File.WriteAllBytes(Path.Combine(photos, "broken.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var (code2, stdout2, _) = await RunAsync([photos, "-r", "--json", "--effort", "fast", "--data-dir", data]);
        var second = stdout2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Equal(1, code2);
        Assert.Equal(3, second.Count);
        var broken = second.Single(e => e.GetProperty("input").GetString()!.EndsWith("broken.png"));
        Assert.Equal("failed", broken.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(broken.GetProperty("error").GetString()));
        Assert.Equal(2, second.Count(e => e.GetProperty("outcome").GetString() == "compressed"));
    }

    [Fact]
    public async Task Cli_TextOutput_OutputFolderMirrorsTheTree()
    {
        EngineTestSupport.RequireTools(ToolId.Cjxl, ToolId.Djxl, ToolId.Cwebp, ToolId.Dwebp, ToolId.Oxipng, ToolId.Ect);
        using var dir = new TempDir("cli-text");
        string src = dir.Sub("src");
        Directory.CreateDirectory(Path.Combine(src, "a", "b"));
        using (var graphic = TestImages.Graphic(120, 80))
        {
            TestImages.SavePngWithWic(graphic, Path.Combine(src, "a", "b", "deep.png"));
            TestImages.SavePngWithWic(graphic, Path.Combine(src, "top.png"));
        }

        string output = Path.Combine(dir.Path, "out");

        var (code, stdout, stderr) = await RunAsync([src, "-r", "-p", "flawless", "-o", output, "--data-dir", dir.Sub("data")]);

        Assert.True(code == 0, stderr + stdout);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("ok  deep.png → deep.", StringComparison.Ordinal) && l.EndsWith("FL pixel-exact", StringComparison.Ordinal));
        Assert.StartsWith("2 file(s): 2 compressed, 0 kept, 0 failed", lines[^1]);
        Assert.Single(Directory.GetFiles(Path.Combine(output, "a", "b"), "deep.*"));
        Assert.Single(Directory.GetFiles(output, "top.*"));
    }

    [Fact]
    public async Task Cli_DryRun_ListsWithoutWriting()
    {
        using var dir = new TempDir("cli-dry");
        using (var px = TestImages.Graphic(40, 30))
        {
            EngineTestSupport.WritePng(dir.File("one.png"), px);
            EngineTestSupport.WritePng(dir.File("two.PNG"), px);
        }

        File.WriteAllText(dir.File("readme.md"), "#");

        var (code, stdout, _) = await RunAsync([Path.Combine(dir.Path, "*.png"), "--dry-run", "--json"]);

        Assert.Equal(0, code);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Equal(["one.png", "two.PNG"], lines.Select(l => Path.GetFileName(l.GetProperty("input").GetString())));
        Assert.All(lines, l => Assert.True(l.GetProperty("dryRun").GetBoolean()));
        Assert.All(lines, l => Assert.Equal(dir.File("Carbonado"), l.GetProperty("outputFolder").GetString()));
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
        Assert.False(Directory.Exists(dir.File("Carbonado")));
    }

    [Fact]
    public async Task Cli_MissingInputs_FailWithExit1()
    {
        using var dir = new TempDir("cli-missing");

        var (code, stdout, stderr) = await RunAsync([dir.File("nope.jpg"), Path.Combine(dir.Path, "*.webp"), "--data-dir", dir.Sub("data")]);

        Assert.Equal(1, code);
        Assert.Contains("fail  " + dir.File("nope.jpg") + "  The file does not exist.", stdout);
        Assert.Contains("nothing to compress", stderr);
    }

    [Fact]
    public async Task Cli_EmptyFolder_NothingToDoExit0()
    {
        using var dir = new TempDir("cli-empty");

        var (code, stdout, stderr) = await RunAsync([dir.Sub("empty"), "--data-dir", dir.Sub("data")]);

        Assert.Equal(0, code);
        Assert.Empty(stdout);
        Assert.Contains("no images found", stderr);
    }

    [Fact]
    public async Task Cli_Cancellation_EndsWithExit1AndNoOutputs()
    {
        EngineTestSupport.RequireTools();
        using var dir = new TempDir("cli-cancel");
        string input = EngineTestSupport.PhotoJpeg(dir, "big.jpg", 800);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1.5));
        var stdout = new StringWriter();

        int code = await CliRunner.RunAsync([input, "--effort", "insane", "--data-dir", dir.Sub("data")], stdout, new StringWriter(), cts.Token);

        Assert.Equal(1, code);
        Assert.Contains("cancelled  big.jpg", stdout.ToString());
        Assert.False(Directory.Exists(dir.File("Carbonado")));
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliRunner.RunAsync(args, stdout, stderr, Ct);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
