using System.Globalization;
using Carbonado.App.Localization;
using Carbonado.App.Pages.Archive;
using Carbonado.App.Pages.Profiles;
using Carbonado.App.Pages.Settings;
using Carbonado.App.Services;
using Carbonado.App.Shell;
using Carbonado.App.Tests.TestSupport;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Migration;
using Carbonado.Core.Persistence;
using Microsoft.Win32;

namespace Carbonado.App.Tests.Pages;

public sealed class OtherViewModelsTests : IDisposable
{
    public OtherViewModelsTests() => Loc.SetLookupOverride(k => k);

    public void Dispose() => Loc.SetLookupOverride(null);

    [Fact]
    public void Profiles_DuplicateEditSaveDelete()
    {
        var settings = new AppSettings();
        int persisted = 0;
        var catalog = new ProfileCatalog(settings, _ => persisted++);
        var dialogs = new FakeDialogs();
        var vm = new ProfilesViewModel(catalog, dialogs);

        Assert.Equal("vs", vm.Selected!.Id);
        Assert.False(vm.IsEditable);
        Assert.True(vm.IsActive);

        vm.DuplicateCommand.Execute(null);
        Assert.Equal("vs-copy", vm.Selected!.Id);
        Assert.True(vm.IsEditable);
        Assert.False(vm.IsDirty);

        vm.EditTargetScore = 92;
        vm.EditEffort = EffortLevel.Insane;
        vm.Formats.Single(f => f.Format == OutputFormat.Jpeg).IsOn = false;
        vm.EditResize = true;
        vm.EditMaxWidth = 1280;
        Assert.True(vm.IsDirty);
        Assert.Equal("VVS", vm.Grade);
        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(settings.CustomProfiles);
        Assert.Equal(92, saved.TargetScore);
        Assert.Equal(EffortLevel.Insane, saved.Effort);
        Assert.DoesNotContain(OutputFormat.Jpeg, saved.Formats);
        Assert.Equal(new ResizeSpec(1280, 2048), saved.Resize);
        Assert.Equal("VVS", saved.Grade);
        Assert.False(vm.IsDirty);

        vm.SetActiveCommand.Execute(null);
        Assert.Equal("vs-copy", settings.ActiveProfileId);

        dialogs.NextConfirm = ConfirmResult.Primary;
        vm.DeleteCommand.Execute(null);
        Assert.Empty(settings.CustomProfiles);
        Assert.Equal("vs", settings.ActiveProfileId);
        Assert.True(persisted >= 4);
    }

    [Fact]
    public void Profiles_ValidationRejectsEmptyNameAndNoFormats()
    {
        var catalog = new ProfileCatalog(new AppSettings(), _ => { });
        var vm = new ProfilesViewModel(catalog, new FakeDialogs());
        vm.DuplicateCommand.Execute(null);
        vm.EditName = "  ";
        Assert.Equal("L.Profiles.Error.Name", vm.Validate());
        vm.EditName = "Mine";
        foreach (var f in vm.Formats)
        {
            f.IsOn = false;
        }

        Assert.Equal("L.Profiles.Error.Formats", vm.Validate());
        vm.SaveCommand.Execute(null);
        Assert.Equal("L.Profiles.Error.Formats", vm.ValidationError);
    }

    [Fact]
    public void Catalog_RefusesToOverwriteBuiltIns() =>
        Assert.Throws<InvalidOperationException>(() => new ProfileCatalog(new AppSettings(), _ => { }).SaveCustom(BuiltInProfiles.Vs));

    [Fact]
    public void Archive_DailySavingsBucketsLast14Days()
    {
        var today = new DateTime(2026, 11, 21, 15, 0, 0);
        HistoryEntry E(int daysAgo, long original, long output, JobOutcome outcome = JobOutcome.Compressed) =>
            new(new DateTimeOffset(today.AddDays(-daysAgo)), "a.png", "a.avif", outcome, original, output, OutputFormat.Avif, 90, "vs", 1);

        var (values, labels) = ArchiveViewModel.Daily([E(0, 1000, 400), E(0, 500, 100), E(13, 300, 100), E(14, 999, 1), E(2, 10, 5, JobOutcome.Failed)], today, CultureInfo.InvariantCulture);

        Assert.Equal(14, values.Length);
        Assert.Equal(1000, values[13]);
        Assert.Equal(200, values[0]);
        Assert.Equal(1200, values.Sum());
        Assert.Equal("21", labels[13]);
        Assert.Equal("08", labels[0]);
    }

    [Fact]
    public async Task Archive_RefreshLoadsStatsSharesAndRows()
    {
        using var dir = new TempFolder();
        var history = new HistoryStore(DataPaths.Resolve(dir.Path, AppContext.BaseDirectory).EnsureCreated());
        history.Append(Result("x.png", JobOutcome.Compressed, 1000, 250, OutputFormat.Avif), "vs");
        history.Append(Result("y.png", JobOutcome.Compressed, 1000, 500, OutputFormat.Jxl), "vs");
        history.Append(Result("z.png", JobOutcome.Compressed, 1000, 500, OutputFormat.Avif), "vs");
        var vm = new ArchiveViewModel(history, new ImmediateDispatcher(), new FakeDialogs(), new FakeShell());

        await vm.RefreshAsync();

        Assert.False(vm.IsEmpty);
        Assert.Equal(3, vm.Stats.Files);
        Assert.Equal(3, vm.Recent.Count);
        Assert.Equal("z.png", vm.Recent[0].FileName);
        Assert.Equal(OutputFormat.Avif, vm.FormatShares[0].Format);
        Assert.Equal(2.0 / 3, vm.FormatShares[0].Fraction, 3);
        Assert.Equal(1750, vm.DailySaved.Sum());
    }

    [Fact]
    public void Settings_ChangesPersistAndApply()
    {
        using var dir = new TempFolder();
        var settings = new AppSettings();
        int saves = 0;
        string? language = null;
        bool? motion = false;
        List<WatchRule>? watched = null;
        var dialogs = new FakeDialogs { FolderToPick = dir.Path };
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory);
        var ctx = new SettingsContext(settings, _ => saves++, dialogs, new FakeShell(), new ProfileCatalog(settings, _ => { }), paths,
            new HistoryStore(paths), new ShellIntegrationService(Registry.CurrentUser, @"Software\Carbonado.Tests\" + Guid.NewGuid().ToString("N")),
            @"C:\Carbonado\Carbonado.exe", rules => watched = [.. rules], _ => Task.FromResult(LegacyScanResult.Empty),
            (_, _) => throw new InvalidOperationException(), l => language = l, m => motion = m);
        var vm = new SettingsViewModel(ctx);
        Assert.Equal(0, saves);

        vm.SelectedLanguage = vm.Languages.Single(o => o.Value == "it");
        Assert.Equal("it", language);
        Assert.Equal("it", settings.Language);

        vm.SelectedMotion = vm.MotionOptions.Single(o => o.Value == true);
        Assert.True(motion);
        vm.SelectedMotion = vm.MotionOptions.Single(o => o.Value is null);
        Assert.Null(motion);

        vm.BrowseCustomFolderCommand.Execute(null);
        Assert.Equal(OutputLocation.CustomFolder, settings.Output.Location);
        Assert.Equal(dir.Path, settings.Output.CustomFolder);

        vm.SelectedConflict = vm.Conflicts.Single(o => o.Value == ConflictPolicy.Skip);
        vm.NamePattern = "{name}-{profile}";
        Assert.Equal(ConflictPolicy.Skip, settings.Output.Conflict);
        Assert.Equal("{name}-{profile}", settings.Output.NamePattern);

        vm.SelectedConcurrency = vm.Concurrency.Single(o => o.Value == 2);
        Assert.Equal(2, settings.MaxConcurrency);

        vm.AddWatchFolderCommand.Execute(null);
        Assert.Single(settings.WatchRules);
        Assert.Single(watched!);
        vm.WatchRules[0].Enabled = false;
        Assert.False(settings.WatchRules[0].Enabled);
        vm.RemoveWatchCommand.Execute(vm.WatchRules[0]);
        Assert.Empty(settings.WatchRules);

        dialogs.NextConfirm = ConfirmResult.Secondary;
        vm.ReplaceOriginals = true;
        Assert.False(vm.ReplaceOriginals);
        Assert.False(settings.Output.ReplaceOriginals);
        Assert.True(saves >= 8);
    }

    [Fact]
    public async Task Settings_MigrationFlow()
    {
        using var dir = new TempFolder();
        var settings = new AppSettings();
        var scan = new LegacyScanResult([new LegacyInstall(dir.Path, null, dir.Path, 3, dir.Path, 0, null, null)], [new LegacyOrphan("o.png", 10, DateTime.UtcNow)]);
        MigrationOptions? applied = null;
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory);
        var dialogs = new FakeDialogs { NextConfirm = ConfirmResult.Primary };
        var ctx = new SettingsContext(settings, _ => { }, dialogs, new FakeShell(), new ProfileCatalog(settings, _ => { }), paths,
            new HistoryStore(paths), new ShellIntegrationService(Registry.CurrentUser, @"Software\Carbonado.Tests\" + Guid.NewGuid().ToString("N")),
            "x.exe", _ => { }, _ => Task.FromResult(scan),
            (s, o) =>
            {
                applied = o;
                return new MigrationReport(3, dir.Path, "legacy-2", 0, false, 1, 10, []);
            }, _ => { }, _ => { });
        var vm = new SettingsViewModel(ctx);

        await vm.ScanLegacyCommand.ExecuteAsync(null);
        Assert.True(vm.HasScan);
        vm.SelectedLevel = vm.Levels.Single(l => l.Value == 2);
        vm.MigrateCleanOrphans = true;
        await vm.ApplyMigrationCommand.ExecuteAsync(null);

        Assert.NotNull(applied);
        Assert.Equal(2, applied!.LegacyLevel);
        Assert.True(applied.CleanOrphans);
        Assert.Single(dialogs.Confirms);
        Assert.Equal("L.Migration.Report", vm.MigrationReportText);
    }

    [Fact]
    public void ShellIntegration_RegistersAndUnregistersUnderTestKey()
    {
        string prefix = @"Software\Carbonado.Tests\" + Guid.NewGuid().ToString("N");
        var svc = new ShellIntegrationService(Registry.CurrentUser, prefix);
        try
        {
            Assert.False(svc.IsRegistered);
            svc.Register(@"C:\Apps\Carbonado\Carbonado.exe", "Compress with Carbonado");
            Assert.True(svc.IsRegistered);
            using (var cmd = Registry.CurrentUser.OpenSubKey(Path.Combine(prefix, ShellIntegrationService.ImageKey, "command")))
            {
                Assert.Equal("\"C:\\Apps\\Carbonado\\Carbonado.exe\" \"%1\"", cmd!.GetValue(null));
            }

            svc.Unregister();
            Assert.False(svc.IsRegistered);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(prefix, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Carbonado.Tests", writable: true);
            if (parent is not null && parent.SubKeyCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Carbonado.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    [Theory]
    [InlineData("compress", "Compress", true)]
    [InlineData("cmp", "Compress", true)]
    [InlineData("xyz", "Compress", false)]
    [InlineData("go set", "Go to: Settings", true)]
    public void Palette_FuzzyScore(string query, string text, bool matches) =>
        Assert.Equal(matches, CommandPaletteViewModel.Score(text, query) >= 0);

    [Fact]
    public void Palette_RanksPrefixFirst_AndRunsSelected()
    {
        int ran = 0;
        var vm = new CommandPaletteViewModel(() =>
        [
            new PaletteCommand("Clear the queue", "Icon.Trash", null, () => { }, Literal: true),
            new PaletteCommand("Compress", "Icon.Play", "Ctrl+Enter", () => ran++, Literal: true),
            new PaletteCommand("Pause", "Icon.Pause", null, () => { }, () => false, Literal: true),
        ]);
        vm.Refresh();
        Assert.Equal(2, vm.Results.Count);
        vm.Query = "com";
        Assert.Equal("Compress", vm.Results[0].Title);
        vm.ExecuteSelectedCommand.Execute(null);
        Assert.Equal(1, ran);
        Assert.Equal(string.Empty, vm.Query);
    }

    [Fact]
    public void Palette_DoesNotBuildCommandsUntilRefreshed()
    {
        int built = 0;
        var vm = new CommandPaletteViewModel(() =>
        {
            built++;
            return [new PaletteCommand("Compress", "Icon.Play", null, () => { }, Literal: true)];
        });
        Assert.Equal(0, built);
        Assert.Empty(vm.Results);
        vm.Refresh();
        Assert.Equal(1, built);
        Assert.Single(vm.Results);
    }

    [Fact]
    public void Shell_NavigatesAllPagesLazily()
    {
        int created = 0;
        var shell = new ShellViewModel(k =>
        {
            created++;
            return k.ToString();
        });
        Assert.Equal(1, created);
        foreach (var kind in Enum.GetValues<PageKind>())
        {
            shell.NavigateCommand.Execute(kind);
            Assert.Equal(kind, shell.Current);
            Assert.Equal(kind.ToString(), shell.CurrentPage);
        }

        shell.NavigateCommand.Execute(PageKind.Forge);
        Assert.Equal(Enum.GetValues<PageKind>().Length, created);
        Assert.All(shell.Navigation, n => Assert.StartsWith("Nav.", n.AutomationId, StringComparison.Ordinal));
    }

    [Fact]
    public void Chime_IsValidWav()
    {
        byte[] wav = SoundService.SynthesizeChime();
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(44 + (int)(44100 * 0.7) * 2, wav.Length);
        Assert.Contains(wav[44..], b => b != 0);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10 * 1024 * 1024, "10.0 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    public void FormatText_Bytes(long bytes, string expected) =>
        Assert.Equal(expected, FormatText.Bytes(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void FormatText_PercentDurationGrade()
    {
        Assert.Equal("42%", FormatText.Percent(0.42, CultureInfo.InvariantCulture));
        Assert.Equal("4.2%", FormatText.Percent(0.042, CultureInfo.InvariantCulture));
        Assert.Equal("4,2%", FormatText.Percent(0.042, CultureInfo.GetCultureInfo("it-IT")));
        Assert.Equal("850 ms", FormatText.Duration(TimeSpan.FromMilliseconds(850), CultureInfo.InvariantCulture));
        Assert.Equal("3 min 05 s", FormatText.Duration(TimeSpan.FromSeconds(185), CultureInfo.InvariantCulture));
        Assert.Equal("VVS", FormatText.GradeFor(90));
        Assert.Equal("VS", FormatText.GradeFor(85));
        Assert.Equal("SI", FormatText.GradeFor(80));
        Assert.Equal("I", FormatText.GradeFor(50));
    }

    private static CompressionResult Result(string input, JobOutcome outcome, long original, long output, OutputFormat? format) =>
        new(Guid.NewGuid(), input, input + ".out", outcome, original, output, format, "t", 90, VerificationKind.PerceptualScore, TimeSpan.FromSeconds(1), [], null, 1, 1);
}
