using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Migration;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Migration;

public class LegacyMigratorTests
{
    [Fact]
    public void Scan_FindsLegacyTree_InNestedBinFolder()
    {
        using var root = new TempDir("legacy");
        string app = root.Sub(@"IMGCompress\bin\Release");
        File.WriteAllText(Path.Combine(app, "IMGCompress.exe"), "MZ");
        File.WriteAllText(Path.Combine(app, "ffmpeg.exe"), "MZ");
        File.WriteAllText(Path.Combine(app, "pngquant.exe"), "MZ");
        string inputs = Directory.CreateDirectory(Path.Combine(app, "inputs")).FullName;
        string outputs = Directory.CreateDirectory(Path.Combine(app, "outputs")).FullName;
        File.WriteAllText(Path.Combine(inputs, "a.png"), "x");
        File.WriteAllText(Path.Combine(inputs, "b.JPG"), "x");
        File.WriteAllText(Path.Combine(inputs, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(outputs, "a.jxl"), "x");

        var scan = new LegacyMigrator().Scan([root.Path], legacyTempDir: root.Sub("noTemp"));

        var install = Assert.Single(scan.Installs);
        Assert.Equal(app, install.Folder);
        Assert.NotNull(install.ExePath);
        Assert.Equal(2, install.InputImageCount);
        Assert.Equal(1, install.OutputImageCount);
        Assert.NotNull(install.FfmpegPath);
        Assert.NotNull(install.PngquantPath);
        Assert.Empty(scan.Orphans);
    }

    [Fact]
    public void Scan_WorkspaceWithoutExe_NeedsFoldersAndTools()
    {
        using var root = new TempDir("legacy");
        string onlyFolders = root.Sub("justFolders");
        Directory.CreateDirectory(Path.Combine(onlyFolders, "inputs"));
        Directory.CreateDirectory(Path.Combine(onlyFolders, "outputs"));
        string withTool = root.Sub("withTool");
        Directory.CreateDirectory(Path.Combine(withTool, "inputs"));
        Directory.CreateDirectory(Path.Combine(withTool, "outputs"));
        File.WriteAllText(Path.Combine(withTool, "pngquant.exe"), "MZ");

        var scan = new LegacyMigrator().Scan([root.Path], legacyTempDir: root.Sub("t"));

        Assert.Equal(withTool, Assert.Single(scan.Installs).Folder);
    }

    [Theory]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.png", true)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b-or8.png", true)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.avif", true)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.jxl", false)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9.png", false)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9bX.png", false)]
    [InlineData("vacation-photo-2024-summer-beach.jpg", false)]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.PNG", false)]
    [InlineData("aB3dE5gH7jK9_N1pQ3sT5vW7yZ9b.png", false)]
    public void OrphanRegexIsStrict(string name, bool expected) =>
        Assert.Equal(expected, LegacyMigrator.OrphanPattern().IsMatch(name));

    [Fact]
    public void Scan_FindsOrphansOnlyMatchingPattern()
    {
        using var root = new TempDir("legacy");
        string temp = root.Sub("Temp");
        File.WriteAllBytes(Path.Combine(temp, "aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.png"), new byte[123]);
        File.WriteAllBytes(Path.Combine(temp, "aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b-or8.png"), new byte[7]);
        File.WriteAllBytes(Path.Combine(temp, "my-important-file.png"), new byte[9]);

        var scan = new LegacyMigrator().Scan([], legacyTempDir: temp);

        Assert.Equal(2, scan.Orphans.Count);
        Assert.Equal(130, scan.OrphanBytes);
    }

    [Fact]
    public void Apply_ImportsMapsLevelRegistersToolsCreatesRuleAndRecycles()
    {
        using var root = new TempDir("legacy");
        string app = root.Sub("app");
        File.WriteAllText(Path.Combine(app, "IMGCompress.exe"), "MZ");
        File.WriteAllText(Path.Combine(app, "ffmpeg.exe"), "MZ");
        string inputs = Directory.CreateDirectory(Path.Combine(app, "inputs")).FullName;
        string outputs = Directory.CreateDirectory(Path.Combine(app, "outputs")).FullName;
        File.WriteAllText(Path.Combine(inputs, "one.png"), "x");
        File.WriteAllText(Path.Combine(inputs, "two.webp"), "x");
        string temp = root.Sub("Temp");
        string orphan = Path.Combine(temp, "aB3dE5gH7jK9mN1pQ3sT5vW7yZ9b.jpg");
        File.WriteAllBytes(orphan, new byte[50]);

        var migrator = new LegacyMigrator();
        var scan = migrator.Scan([root.Path], temp);
        var settings = new AppSettings();
        var queued = new List<string>();
        var bin = new FakeRecycleBin();

        var report = migrator.Apply(scan, new MigrationOptions(true, true, 3, true, true, true), settings, queued.AddRange, bin);

        Assert.Equal(2, report.ImportedFiles);
        Assert.Equal(2, queued.Count);
        Assert.Equal(outputs, report.Destination);
        Assert.Equal(OutputLocation.CustomFolder, settings.Output.Location);
        Assert.Equal(outputs, settings.Output.CustomFolder);
        Assert.Equal("legacy-3", report.ProfileId);
        Assert.Equal("legacy-3", settings.ActiveProfileId);
        Assert.Equal(1, report.ToolsRegistered);
        Assert.Contains(app, settings.ExtraToolDirs);
        Assert.True(report.WatchRuleCreated);
        var rule = Assert.Single(settings.WatchRules);
        Assert.Equal(inputs, rule.Folder);
        Assert.Equal(outputs, rule.Output.CustomFolder);
        Assert.Equal(1, report.OrphansRecycled);
        Assert.Equal(50, report.BytesFreed);
        Assert.Equal([orphan], bin.Sent);
        Assert.True(settings.LegacyScanDone);

        // Idempotent second run: no duplicate rule or tool dir.
        var again = migrator.Apply(scan, new MigrationOptions(false, false, 3, true, true, false), settings, _ => { }, bin);
        Assert.Equal(0, again.ToolsRegistered);
        Assert.False(again.WatchRuleCreated);
        Assert.Single(settings.WatchRules);
    }

    [Theory]
    [InlineData(1, OutputFormat.Jpeg)]
    [InlineData(2, OutputFormat.WebP)]
    [InlineData(3, OutputFormat.Avif)]
    [InlineData(4, OutputFormat.Jxl)]
    [InlineData(99, OutputFormat.Jxl)]
    public void LegacyLevels_MapFormats(int level, OutputFormat format)
    {
        var p = LegacyLevels.ProfileFor(level);
        Assert.Equal(format, p.Formats[0]);
        Assert.Equal(FormatStrategy.Fixed, p.Strategy);
        Assert.Equal(80, p.TargetScore);
        Assert.True(p.BuiltIn);
        Assert.StartsWith("legacy-", p.Id, StringComparison.Ordinal);
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Sent { get; } = [];

        public bool Send(string path)
        {
            Sent.Add(path);
            return true;
        }
    }
}
