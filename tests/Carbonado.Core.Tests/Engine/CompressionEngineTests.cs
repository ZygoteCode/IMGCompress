using System.Security.Cryptography;
using Carbonado.Core.Codecs;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Metrics;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.Decoding;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;
using static Carbonado.Core.Tests.Engine.EngineTestSupport;

namespace Carbonado.Core.Tests.Engine;

public class CompressionEngineTests
{
    [Fact]
    public async Task Engine_Photo_MeetsTargetAndShrinks()
    {
        RequireTools();
        using var dir = new TempDir("engine-photo");
        string input = PhotoJpeg(dir);
        using var ctx = Context(dir);
        var progress = new List<JobProgress>();

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs, progress: progress);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(VerificationKind.PerceptualScore, result.Verification);
        Assert.NotNull(result.Score);
        Assert.True(result.Score >= 85, $"score {result.Score}");
        Assert.True(result.OutputBytes < result.OriginalBytes);
        Assert.Equal(new FileInfo(input).Length, result.OriginalBytes);
        Assert.Equal(new FileInfo(result.OutputPath!).Length, result.OutputBytes);
        Assert.Equal(Path.Combine(dir.Path, "Carbonado", "photo" + FormatInfo.Extension(result.Format!.Value)), result.OutputPath);
        Assert.Contains(result.Candidates, c => c.Kind == EncodeKind.Lossy && c.Passed);

        // Independent check: the written file, decoded again, scores what the engine reported.
        var loader = FormatFixtures.Loader(dir);
        using var source = await loader.LoadAsync(input, new LoadOptions(), Ct);
        using var output = await loader.DecodeOutputAsync(result.OutputPath!, result.Format.Value, Ct);
        Assert.Equal(result.Score!.Value, Ssimulacra2.Compute(source.Pixels, output, ct: Ct), 6);

        // Nothing but the output was written next to the source; the job's temp folder is gone.
        Assert.Equal([Path.Combine("Carbonado", Path.GetFileName(result.OutputPath!)), "photo.jpg"], UserFiles(dir));
        Assert.Empty(Directory.EnumerateDirectories(ctx.SessionTemp, "job-*"));
        AssertProgressIsOrdered(progress);
    }

    [Theory]
    [InlineData("graphic")]
    [InlineData("alpha")]
    [InlineData("deep")]
    public async Task Engine_Lossless_IsVerifiedPixelExact(string image)
    {
        RequireTools();
        using var dir = new TempDir("engine-lossless");
        using var px = image switch
        {
            "graphic" => TestImages.Graphic(320, 200),
            "alpha" => TestImages.WithAlpha(200, 150),
            _ => TestImages.Deep16(160, 120),
        };

        // WIC writes plain, unoptimized PNGs: every lossless candidate beats them.
        string input = dir.File(image + ".png");
        TestImages.SavePngWithWic(px, input);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Flawless);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(VerificationKind.PixelExact, result.Verification);
        Assert.Null(result.Score);
        Assert.True(result.OutputBytes < result.OriginalBytes);
        Assert.All(result.Candidates, c => Assert.NotEqual(EncodeKind.Lossy, c.Kind));

        using var decoded = await FormatFixtures.Loader(dir).DecodeOutputAsync(result.OutputPath!, result.Format!.Value, Ct);
        PixelAssert.VisiblyIdentical(px, decoded);
    }

    [Fact]
    public async Task Engine_JpegToJxl_BitExact()
    {
        RequireTools(ToolId.Cjxl, ToolId.Djxl);
        using var dir = new TempDir("engine-transcode");
        string input = dir.File("camera.jpg");
        using (var px = TestImages.PhotoLike(320, 240))
        {
            TestImages.SaveJpegWithWic(px, input, 90);
        }

        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.JpegArchive);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(OutputFormat.Jxl, result.Format);
        Assert.Equal("jxl.cjxl", result.EncoderId);
        Assert.Equal(VerificationKind.BitExactReconstruction, result.Verification);
        Assert.Single(result.Candidates, c => c.Kind == EncodeKind.JpegTranscode && c.Passed);

        // Independent reconstruction with djxl: the very same JPEG comes back.
        string rebuilt = dir.File("rebuilt.jpg");
        await FormatFixtures.Run(ToolId.Djxl, [result.OutputPath!, rebuilt, "-J", "--quiet"], Ct);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(input)), SHA256.HashData(File.ReadAllBytes(rebuilt)));
    }

    [Theory]
    [InlineData(OutputLocation.SubfolderNextToSource, false)]
    [InlineData(OutputLocation.CustomFolder, false)]
    [InlineData(OutputLocation.SameFolder, false)]
    [InlineData(OutputLocation.SubfolderNextToSource, true)]
    public async Task Engine_NeverLarger_KeepsOriginal(OutputLocation location, bool replace)
    {
        RequireTools(ToolId.Oxipng, ToolId.Ect);
        using var dir = new TempDir("engine-neverlarger");
        string input = await OptimalPngAsync(dir);
        byte[] before = File.ReadAllBytes(input);
        var plan = new OutputPlan(location, CustomFolder: dir.File("out"), ReplaceOriginals: replace);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Keep with { Mode = QualityMode.Lossless }, plan);

        Assert.True(result.Outcome == JobOutcome.KeptOriginal, $"{result.Outcome}: {result.Error}");
        Assert.Equal(result.OriginalBytes, result.OutputBytes);
        Assert.NotNull(result.Error); // the note says why
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.NotEmpty(result.Candidates);
        bool writesElsewhere = !replace && location != OutputLocation.SameFolder;
        if (writesElsewhere)
        {
            // The output folder is complete: the original is copied there under its own name.
            string expected = location == OutputLocation.CustomFolder ? dir.File(Path.Combine("out", "tiny.png")) : dir.File(Path.Combine("Carbonado", "tiny.png"));
            Assert.Equal(expected, result.OutputPath);
            Assert.Equal(before, File.ReadAllBytes(expected));
            Assert.Equal(2, UserFiles(dir).Length);
        }
        else
        {
            Assert.Null(result.OutputPath);
            Assert.Equal(["tiny.png"], UserFiles(dir));
        }
    }

    [Fact]
    public async Task Engine_CorruptInputs_FailPerFile()
    {
        RequireTools();
        using var dir = new TempDir("engine-corrupt");
        string empty = dir.File("empty.png");
        File.WriteAllBytes(empty, []);
        string noise = dir.File("noise.jpg");
        File.WriteAllBytes(noise, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251))]);
        string full = PhotoJpeg(dir, "full.jpg", 320);
        string truncated = dir.File("half-downloaded.jpg");
        byte[] jpeg = File.ReadAllBytes(full);
        File.WriteAllBytes(truncated, jpeg[..(jpeg.Length / 2)]);
        string misnamed = dir.File("really-a-png.jpg");
        using (var px = TestImages.Graphic(120, 80))
        {
            WritePng(misnamed, px);
        }

        string missing = dir.File("missing.png");
        using var ctx = Context(dir);

        // All at once: each file fails (or succeeds) on its own.
        var results = await Task.WhenAll(new[] { empty, noise, truncated, misnamed, missing }.Select(p => RunAsync(ctx, p, BuiltInProfiles.Vs)));

        Assert.Equal(JobOutcome.Failed, results[0].Outcome);
        Assert.Equal("The file is empty.", results[0].Error);
        Assert.Equal(JobOutcome.Failed, results[1].Outcome);
        Assert.Contains("not a recognised image", results[1].Error);
        Assert.Equal(JobOutcome.Failed, results[2].Outcome);
        Assert.Contains("truncated", results[2].Error);
        Assert.Equal(JobOutcome.Failed, results[4].Outcome);
        Assert.Equal("The file does not exist.", results[4].Error);
        Assert.All(results.Where(r => r.Outcome == JobOutcome.Failed), r => Assert.Null(r.OutputPath));

        // Content decides, not the name: the PNG called .jpg is compressed like any PNG.
        Assert.True(results[3].Outcome == JobOutcome.Compressed, $"{results[3].Outcome}: {results[3].Error}");
        Assert.True(File.Exists(results[3].OutputPath));
        Assert.Equal(120, results[3].Width);
    }

    [Fact]
    public async Task Engine_LongUnicodePath_Roundtrip()
    {
        RequireTools();
        using var dir = new TempDir("engine-unicode");
        string folder = dir.Path;
        while (folder.Length < 300)
        {
            folder = Path.Combine(folder, "Fotografía ñ 日本語 🎉 Ünïcødé");
        }

        Directory.CreateDirectory(folder);
        string input = Path.Combine(folder, "Ritratto — été 写真.png");
        Assert.True(input.Length > 260);
        using (var px = TestImages.Graphic(96, 64))
        {
            TestImages.SavePngWithWic(px, dir.File("seed.png"));
            File.Copy(dir.File("seed.png"), input);
        }

        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs with { Effort = EffortLevel.Fast });

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(Path.Combine(folder, "Carbonado", "Ritratto — été 写真" + FormatInfo.Extension(result.Format!.Value)), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        using var decoded = await FormatFixtures.Loader(dir).DecodeOutputAsync(result.OutputPath!, result.Format.Value, Ct);
        Assert.Equal((96, 64), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task Engine_KeepsIccProfile()
    {
        RequireTools();
        using var dir = new TempDir("engine-icc");
        byte[] p3 = TestIcc.DisplayP3Gamma22();
        using var px = TestImages.PhotoLike(200, 150, seed: 3);
        string input = WritePng(dir.File("p3.png"), px, new ImageMetadata { Icc = p3 });
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        using var output = await FormatFixtures.Loader(dir).LoadAsync(result.OutputPath!, new LoadOptions(), Ct);
        Assert.NotNull(output.Metadata.Icc);
        Assert.True(IccHeader.IsRgb(output.Metadata.Icc));
    }

    [Theory]
    [InlineData(OutputFormat.WebP)]
    [InlineData(OutputFormat.Jpeg)]
    [InlineData(OutputFormat.Jxl)]
    public async Task Engine_StripAll_RemovesExif(OutputFormat format)
    {
        RequireTools();
        using var dir = new TempDir("engine-strip");
        string input = JpegWithMetadata(dir);
        var profile = BuiltInProfiles.Vs with { Strategy = FormatStrategy.Fixed, Formats = [format], Metadata = MetadataPolicy.StripAll, AllowJpegTranscode = false };
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, profile);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(format, result.Format);
        using var output = await FormatFixtures.Loader(dir).LoadAsync(result.OutputPath!, new LoadOptions(), Ct);
        Assert.Null(output.Metadata.Exif);
        Assert.Null(output.Metadata.Xmp);
        Assert.Null(output.Metadata.Icc);
        Assert.True(File.ReadAllBytes(result.OutputPath!).AsSpan().IndexOf(ExifTestData.Latitude) < 0);
    }

    [Theory]
    [InlineData(OutputFormat.WebP)]
    [InlineData(OutputFormat.Jpeg)]
    public async Task Engine_KeepAllButGps_KeepsExifWithoutLocation(OutputFormat format)
    {
        RequireTools();
        using var dir = new TempDir("engine-gps");
        string input = JpegWithMetadata(dir);
        var profile = BuiltInProfiles.Vs with { Strategy = FormatStrategy.Fixed, Formats = [format], Metadata = MetadataPolicy.KeepAllButGps, AllowJpegTranscode = false };
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, profile);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        using var output = await FormatFixtures.Loader(dir).LoadAsync(result.OutputPath!, new LoadOptions(), Ct);
        Assert.NotNull(output.Metadata.Exif);
        Assert.False(ExifReader.HasGps(output.Metadata.Exif));
        Assert.Equal(1, ExifReader.ReadOrientation(output.Metadata.Exif));
        Assert.True(File.ReadAllBytes(result.OutputPath!).AsSpan().IndexOf(ExifTestData.Latitude) < 0);
        Assert.NotNull(output.Metadata.Xmp);
        string xmp = System.Text.Encoding.UTF8.GetString(output.Metadata.Xmp);
        Assert.DoesNotContain("GPSLatitude", xmp);
        Assert.Contains("image/jpeg", xmp);
    }

    [Fact]
    public async Task Engine_AnimatedGif_StaysAnimated()
    {
        RequireTools(ToolId.Gif2webp, ToolId.Cjxl, ToolId.Djxl, ToolId.Dwebp);
        using var dir = new TempDir("engine-anim");
        string input = dir.File("spinner.gif");
        using (PixelBuffer a = TestImages.Palette(96, 64, 12), b = TestImages.Palette(96, 64, 24), c = TestImages.Graphic(96, 64))
        {
            FormatFixtures.AnimatedGif(input, a, b, c);
        }

        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Contains(result.Format, new OutputFormat?[] { OutputFormat.WebP, OutputFormat.Jxl });
        Assert.Equal(VerificationKind.PixelExact, result.Verification);
        Assert.All(result.Candidates, c => Assert.Contains(c.EncoderId, new[] { "webp.gif2webp", "jxl.cjxl" }));
        using var output = await FormatFixtures.Loader(dir).LoadAsync(result.OutputPath!, new LoadOptions(), Ct);
        Assert.Equal(3, output.FrameCount);
    }

    [Fact]
    public async Task Engine_AnimatedWithoutAnimatedFormat_KeepsOriginalWithNote()
    {
        using var dir = new TempDir("engine-anim-keep");
        string input = dir.File("spinner.gif");
        using (PixelBuffer a = TestImages.Palette(48, 32, 8), b = TestImages.Palette(48, 32, 16))
        {
            FormatFixtures.AnimatedGif(input, a, b);
        }

        using var ctx = Context(dir);

        // AVIF, JPEG and PNG have no path that keeps a GIF's frames.
        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs with { Formats = [OutputFormat.Avif, OutputFormat.Jpeg, OutputFormat.Png] });

        Assert.Equal(JobOutcome.KeptOriginal, result.Outcome);
        Assert.Contains("Animated", result.Error);
        Assert.Empty(result.Candidates);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public async Task Engine_Resize_FitsWithin()
    {
        RequireTools();
        using var dir = new TempDir("engine-resize");
        using var px = TestImages.PhotoLike(320, 240, seed: 5);
        string input = WritePng(dir.File("wide.png"), px);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs with { Resize = new ResizeSpec(160, 160) });

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal((160, 120), (result.Width, result.Height));
        using var decoded = await FormatFixtures.Loader(dir).DecodeOutputAsync(result.OutputPath!, result.Format!.Value, Ct);
        Assert.Equal((160, 120), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task Engine_Resize_DropsCoefficientPreservingJpegPaths()
    {
        RequireTools();
        using var dir = new TempDir("engine-resize-jpeg");
        string input = PhotoJpeg(dir, "photo.jpg", 400);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs with { Resize = new ResizeSpec(200, 200) });

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.DoesNotContain(result.Candidates, c => c.Kind is EncodeKind.JpegTranscode or EncodeKind.Optimize);
        Assert.Equal(200, Math.Max(result.Width, result.Height));
    }

    [Fact]
    public async Task Engine_TargetSize_UnderBudget()
    {
        RequireTools();
        using var dir = new TempDir("engine-size");
        using var px = TestImages.PhotoLike(320, 240, seed: 9);
        string input = WritePng(dir.File("mail.png"), px);
        const long budget = 9_000;
        var profile = BuiltInProfiles.Email with { TargetBytes = budget, Resize = null };
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, profile);

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.InRange(result.OutputBytes, 1, budget);
        Assert.Contains(result.Format, new OutputFormat?[] { OutputFormat.Jpeg, OutputFormat.WebP });
        Assert.NotNull(result.Score);

        // The best score among the candidates that fit was kept.
        double bestFitting = result.Candidates.Where(c => c.Bytes > 0 && c.Bytes <= budget && c.Score is not null).Max(c => c.Score!.Value);
        Assert.Equal(bestFitting, result.Score!.Value, 9);
    }

    [Fact]
    public async Task Engine_ReplaceOriginal_BacksUp()
    {
        RequireTools();
        using var dir = new TempDir("engine-replace");
        using var px = TestImages.PhotoLike(240, 160, seed: 11);
        string input = WritePng(dir.File("holiday.png"), px);
        byte[] original = File.ReadAllBytes(input);
        var stamp = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(input, stamp);
        File.SetLastWriteTimeUtc(input, stamp);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Vs, new OutputPlan(ReplaceOriginals: true));

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        string expected = dir.File("holiday" + FormatInfo.Extension(result.Format!.Value));
        Assert.Equal(expected, result.OutputPath);
        Assert.False(File.Exists(input));
        string backup = Path.Combine(dir.Path, OutputPathResolver.BackupFolderName, "holiday.png");
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(expected));
        Assert.Equal(stamp, File.GetCreationTimeUtc(expected));

        // A second run (new original, same name) never overwrites the first backup.
        WritePng(input, px);
        var again = await RunAsync(ctx, input, BuiltInProfiles.Vs with { Strategy = FormatStrategy.KeepSource }, new OutputPlan(ReplaceOriginals: true));
        Assert.Contains(again.Outcome, new[] { JobOutcome.Compressed, JobOutcome.KeptOriginal });
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task Engine_ConflictPolicies()
    {
        RequireTools();
        using var dir = new TempDir("engine-conflicts");
        using var px = TestImages.Graphic(120, 80);
        string input = WritePng(dir.File("logo.bmp.png"), px);
        var profile = BuiltInProfiles.Flawless with { Strategy = FormatStrategy.Fixed, Formats = [OutputFormat.WebP] };
        using var ctx = Context(dir);

        var first = await RunAsync(ctx, input, profile);
        var renamed = await RunAsync(ctx, input, profile);
        var skipped = await RunAsync(ctx, input, profile, new OutputPlan(Conflict: ConflictPolicy.Skip));
        var overwritten = await RunAsync(ctx, input, profile, new OutputPlan(Conflict: ConflictPolicy.Overwrite));

        string folder = dir.File("Carbonado");
        Assert.Equal(Path.Combine(folder, "logo.bmp.webp"), first.OutputPath);
        Assert.Equal(Path.Combine(folder, "logo.bmp (1).webp"), renamed.OutputPath);
        Assert.Equal(JobOutcome.Skipped, skipped.Outcome);
        Assert.Null(skipped.OutputPath);
        Assert.NotNull(skipped.Error);
        Assert.Equal(Path.Combine(folder, "logo.bmp.webp"), overwritten.OutputPath);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Engine_SameFolderOverwrite_NeverOverwritesTheOriginal()
    {
        RequireTools(ToolId.Oxipng, ToolId.Ect);
        using var dir = new TempDir("engine-self");
        using var px = TestImages.Graphic(160, 100);
        string input = dir.File("ui.png");
        TestImages.SavePngWithWic(px, input);
        byte[] original = File.ReadAllBytes(input);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Keep with { Mode = QualityMode.Lossless }, new OutputPlan(OutputLocation.SameFolder, Conflict: ConflictPolicy.Overwrite));

        Assert.True(result.Outcome == JobOutcome.Compressed, $"{result.Outcome}: {result.Error}");
        Assert.Equal(dir.File("ui (1).png"), result.OutputPath);
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public async Task Engine_ReadOnlyDestination_FailsThisFileOnly()
    {
        RequireTools();
        using var dir = new TempDir("engine-readonly");
        using var px = TestImages.Graphic(80, 60);
        string input = dir.File("x.png");
        TestImages.SavePngWithWic(px, input);
        string blocker = dir.File("Carbonado");
        File.WriteAllBytes(blocker, [1]); // a file where the output folder should be
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Flawless);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.StartsWith("The output cannot be written", result.Error);
        Assert.Equal(["Carbonado", "x.png"], UserFiles(dir));
    }

    [Fact]
    public async Task Engine_Cancellation_WritesNothingAndCleansUp()
    {
        RequireTools();
        using var dir = new TempDir("engine-cancel");
        string input = PhotoJpeg(dir, "big.jpg", 800);
        using var ctx = Context(dir);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new List<JobProgress>();
        var engine = new CompressionEngine(ctx);
        int armed = 0;
        var reporter = new SyncProgress<JobProgress>(p =>
        {
            lock (progress)
            {
                progress.Add(p);
            }

            if (p.Stage == JobStage.Encoding && Interlocked.Exchange(ref armed, 1) == 0)
            {
                cts.CancelAfter(300);
            }
        });

        var result = await engine.RunAsync(CompressionJob.Create(input, BuiltInProfiles.Vs with { Effort = EffortLevel.Insane }, new OutputPlan()), reporter, 4, cts.Token);

        Assert.Equal(JobOutcome.Cancelled, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal(["big.jpg"], UserFiles(dir));
        Assert.Equal(0, ctx.Runner.ActiveCount);
        Assert.Empty(Directory.EnumerateDirectories(ctx.SessionTemp, "job-*"));
        Assert.DoesNotContain(progress, p => p.Stage == JobStage.Done);
    }

    [Fact]
    public async Task Engine_ProfileWithoutSizeBudget_Fails()
    {
        using var dir = new TempDir("engine-badprofile");
        using var px = TestImages.Graphic(40, 30);
        string input = WritePng(dir.File("a.png"), px);
        using var ctx = Context(dir);

        var result = await RunAsync(ctx, input, BuiltInProfiles.Email with { TargetBytes = null });

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Contains("size budget", result.Error);
    }

    [Fact]
    public void Engine_EstimatesMemoryFromTheHeader()
    {
        using var dir = new TempDir("engine-estimate");
        using var ctx = Context(dir);
        var engine = new CompressionEngine(ctx);
        using var opaque = TestImages.Gradient(800, 600);
        using var alpha = TestImages.WithAlpha(800, 600);
        string opaquePng = WritePng(dir.File("o.png"), opaque);
        string alphaPng = WritePng(dir.File("a.png"), alpha);

        long Estimate(string path, CompressionProfile p) => engine.EstimateMemoryBytes(CompressionJob.Create(path, p, new OutputPlan()));

        const long pixels = 800 * 600;
        Assert.Equal(pixels * 64, Estimate(opaquePng, BuiltInProfiles.Vs));
        Assert.Equal(pixels * 112, Estimate(alphaPng, BuiltInProfiles.Vs));
        Assert.True(Estimate(opaquePng, BuiltInProfiles.Flawless) < Estimate(opaquePng, BuiltInProfiles.Vs));

        // A 24 MP header without decoding anything: 6000 × 4000 × 64.
        string big = dir.File("big.png");
        File.WriteAllBytes(big, PngHeaderOnly(6000, 4000));
        Assert.Equal(6000L * 4000 * 64, Estimate(big, BuiltInProfiles.Vs));
        Assert.True(Estimate(big, BuiltInProfiles.Social) < Estimate(big, BuiltInProfiles.Vs));
    }

    private static void AssertProgressIsOrdered(List<JobProgress> progress)
    {
        Assert.NotEmpty(progress);
        Assert.Equal(JobStage.Probing, progress[0].Stage);
        Assert.Equal(JobStage.Done, progress[^1].Stage);
        Assert.Equal(1.0, progress[^1].Fraction);
        for (int i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i].Stage >= progress[i - 1].Stage, $"stage {progress[i].Stage} after {progress[i - 1].Stage}");
        }

        Assert.Contains(progress, p => p.Stage == JobStage.Encoding && p.CandidateId is not null && p.CandidateScore is not null && p.CandidateBytes is not null);
        Assert.All(progress.Where(p => p.Stage == JobStage.Encoding), p => Assert.InRange(p.Fraction, 0.20, 0.90));
    }

    // A PNG that neither oxipng nor ECT can shrink: the smallest of both at their strongest settings.
    private static async Task<string> OptimalPngAsync(TempDir dir)
    {
        string path = dir.File("tiny.png");
        string work = dir.Sub("seed");
        using (var px = TestImages.Palette(24, 16, 4))
        {
            WritePng(Path.Combine(work, "a.png"), px);
        }

        File.Copy(Path.Combine(work, "a.png"), Path.Combine(work, "b.png"));
        await FormatFixtures.Run(ToolId.Oxipng, ["-o", "max", "--strip", "all", "-z", "-q", "a.png"], Ct, work);
        await FormatFixtures.Run(ToolId.Ect, ["-quiet", "-9", "-strip", "--allfilters-b", "b.png"], Ct, work);
        string best = new[] { "a.png", "b.png" }.Select(f => Path.Combine(work, f)).MinBy(f => new FileInfo(f).Length)!;
        File.Copy(best, path);
        Directory.Delete(work, recursive: true);
        return path;
    }

    // A photo-like JPEG carrying EXIF (orientation 1, camera make, GPS) and XMP with a GPS property.
    private static string JpegWithMetadata(TempDir dir)
    {
        string plain = dir.File("plain.jpg");
        using (var px = TestImages.PhotoLike(160, 120, seed: 21))
        {
            TestImages.SaveJpegWithWic(px, plain, 92);
        }

        byte[] exif = ExifTestData.Build(bigEndian: false, orientation: 1, withGps: true);
        byte[] xmp = System.Text.Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" exif:GPSLatitude=\"45,27.5N\">"
            + "<dc:format>image/jpeg</dc:format><exif:GPSLongitude>9,11.2E</exif:GPSLongitude></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        string path = dir.File("camera.jpg");
        File.WriteAllBytes(path, JpegSegments.Rewrite(File.ReadAllBytes(plain), exif, xmp, null, keepOtherMetadata: false));
        File.Delete(plain);
        return path;
    }

    private static byte[] PngHeaderOnly(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(ms, "IHDR", PngTestFiles.Ihdr(width, height, 8, 2));
        return ms.ToArray();
    }
}
