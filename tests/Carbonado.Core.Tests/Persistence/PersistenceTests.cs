using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Persistence;

public class PersistenceTests
{
    [Fact]
    public void Settings_RoundTrip()
    {
        using var dir = new TempDir("settings");
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory).EnsureCreated();
        using var store = new SettingsStore(paths);
        var s = new AppSettings
        {
            Language = "it",
            Intro = IntroPreference.Short,
            ReducedMotion = true,
            ActiveProfileId = "vvs",
            MaxConcurrency = 3,
            Output = new OutputPlan(OutputLocation.CustomFolder, @"D:\Out", NamePattern: "{name}-{profile}", Conflict: ConflictPolicy.Overwrite),
            CustomProfiles =
            [
                new CompressionProfile
                {
                    Id = "mine", Name = "Mine", NameIt = "Mio", TargetScore = 88.5, Effort = EffortLevel.Max,
                    Formats = [OutputFormat.Avif, OutputFormat.Jxl], Resize = new ResizeSpec(1920, 1080),
                    Metadata = MetadataPolicy.KeepAllButGps,
                },
            ],
            WatchRules = [new WatchRule("w1", @"C:\In", true, "vs", new OutputPlan(), true)],
            ExtraToolDirs = [@"C:\Legacy"],
        };

        store.Save(s);
        var loaded = store.Load();

        Assert.Equal("it", loaded.Language);
        Assert.Equal(IntroPreference.Short, loaded.Intro);
        Assert.True(loaded.ReducedMotion);
        Assert.Equal(3, loaded.MaxConcurrency);
        Assert.Equal(s.Output, loaded.Output);
        var p = Assert.Single(loaded.CustomProfiles);
        Assert.Equal("Mio", p.NameIt);
        Assert.Equal(88.5, p.TargetScore);
        Assert.Equal(new[] { OutputFormat.Avif, OutputFormat.Jxl }, p.Formats);
        Assert.Equal(new ResizeSpec(1920, 1080), p.Resize);
        Assert.Equal(MetadataPolicy.KeepAllButGps, p.Metadata);
        Assert.Single(loaded.WatchRules);
        Assert.Equal(@"C:\Legacy", Assert.Single(loaded.ExtraToolDirs));
        Assert.Contains("\"language\": \"it\"", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Settings_MissingFile_GivesDefaults()
    {
        using var dir = new TempDir("settings");
        using var store = new SettingsStore(DataPaths.Resolve(dir.Path, AppContext.BaseDirectory));
        var s = store.Load();
        Assert.Equal("auto", s.Language);
        Assert.Equal("vs", s.ActiveProfileId);
        Assert.Equal(IntroPreference.Cinematic, s.Intro);
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackToDefaults_AndQuarantines()
    {
        using var dir = new TempDir("settings");
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory).EnsureCreated();
        File.WriteAllText(paths.SettingsFile, "{ \"language\": \"it\", oops");
        using var store = new SettingsStore(paths);
        var s = store.Load();
        Assert.Equal("auto", s.Language);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.Single(Directory.GetFiles(paths.Root, "settings.json.bad-*"));
    }

    [Fact]
    public void Settings_DebounceCoalesces_AndFlushWrites()
    {
        using var dir = new TempDir("settings");
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory).EnsureCreated();
        using var store = new SettingsStore(paths);
        int saves = 0;
        store.Saved += (_, _) => Interlocked.Increment(ref saves);
        var s = new AppSettings();
        for (int i = 0; i < 20; i++)
        {
            s.MaxConcurrency = i + 1;
            store.SaveDebounced(s, TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, saves);
        store.Flush();
        Assert.Equal(1, saves);
        Assert.Equal(20, store.Load().MaxConcurrency);
    }

    [Fact]
    public void History_AppendReadRecentAndStats()
    {
        using var dir = new TempDir("history");
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory).EnsureCreated();
        var history = new HistoryStore(paths);
        for (int i = 0; i < 300; i++)
        {
            history.Append(Result($"img{i}.png", JobOutcome.Compressed, 1000, 400, OutputFormat.Avif), "vs");
        }

        history.Append(Result("kept.png", JobOutcome.KeptOriginal, 500, 500, null), "vs");
        history.Append(Result("bad.png", JobOutcome.Failed, 700, 0, null), "vs");
        history.Append(Result("cancel.png", JobOutcome.Cancelled, 900, 0, null), "vs");

        var recent = history.ReadRecent(5);
        Assert.Equal(5, recent.Count);
        Assert.Equal("bad.png", recent[0].Input);
        Assert.Equal("kept.png", recent[1].Input);
        Assert.Equal("img299.png", recent[2].Input);

        var stats = history.Stats();
        Assert.Equal(301, stats.Files);
        Assert.Equal((300 * 1000) + 500, stats.OriginalBytes);
        Assert.Equal((300 * 400) + 500, stats.OutputBytes);
        Assert.Equal(300, stats.ByFormat[OutputFormat.Avif]);

        // Stats rebuild from the history when the stats file is lost.
        File.Delete(paths.StatsFile);
        var reloaded = new HistoryStore(paths).Stats();
        Assert.Equal(stats.Files, reloaded.Files);
        Assert.Equal(stats.OutputBytes, reloaded.OutputBytes);
    }

    [Fact]
    public void DataPaths_PortableFlag_And_Override()
    {
        using var app = new TempDir("app");
        var normal = DataPaths.Resolve(null, app.Path);
        Assert.False(normal.IsPortable);
        Assert.EndsWith("Carbonado", normal.Root, StringComparison.Ordinal);

        File.WriteAllText(app.File(DataPaths.PortableFlagFile), "");
        var portable = DataPaths.Resolve(null, app.Path);
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(app.Path, "data"), portable.Root);
        Assert.Equal(portable.Root, portable.LocalRoot);

        var overridden = DataPaths.Resolve(app.Sub("custom"), app.Path);
        Assert.False(overridden.IsPortable);
        Assert.Equal(Path.Combine(app.Path, "custom"), overridden.Root);
    }

    [Fact]
    public void SessionTemp_CreatesAliveFolder_AndCleansStale()
    {
        using var root = new TempDir("temp");
        string mine = SessionTemp.Create(root.Path);
        string dead = Path.Combine(root.Path, "999999-12345-abcdef");
        string junk = Path.Combine(root.Path, "not-a-session");
        Directory.CreateDirectory(dead);
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(dead, "x.png"), "x");

        Assert.True(SessionTemp.IsOwnerAlive(Path.GetFileName(mine)));
        int removed = SessionTemp.CleanupStale(root.Path, TimeSpan.FromDays(7));

        Assert.Equal(2, removed);
        Assert.True(Directory.Exists(mine));
        Assert.False(Directory.Exists(dead));
        SessionTemp.Delete(mine);
        Assert.False(Directory.Exists(mine));
    }

    private static CompressionResult Result(string input, JobOutcome outcome, long original, long output, OutputFormat? format) =>
        new(Guid.NewGuid(), input, outcome == JobOutcome.Compressed ? input + ".out" : null, outcome, original, output, format,
            "test", 90, VerificationKind.PerceptualScore, TimeSpan.FromSeconds(1.5), [], null, 10, 10);
}
