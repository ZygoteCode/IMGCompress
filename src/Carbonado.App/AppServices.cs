using System.Windows;
using System.Windows.Threading;
using Carbonado.App.Localization;
using Carbonado.App.Pages.About;
using Carbonado.App.Pages.Archive;
using Carbonado.App.Pages.Compare;
using Carbonado.App.Pages.Engines;
using Carbonado.App.Pages.Forge;
using Carbonado.App.Pages.Profiles;
using Carbonado.App.Pages.Settings;
using Carbonado.App.Services;
using Carbonado.App.Shell;
using Carbonado.App.Startup;
using Carbonado.Core.Engine;
using Carbonado.Core.Logging;
using Carbonado.Core.Migration;
using Carbonado.Core.Persistence;
using Carbonado.UI.Motion;

namespace Carbonado.App;

/// <summary>
/// Composition root. <see cref="Create"/> is synchronous and cheap (settings + stores only) so the window can appear
/// at once; everything heavy (logging, temp cleanup, encoder discovery, engine, watchers, legacy scan) runs in
/// <see cref="StartBackground"/> after the first frame.
/// </summary>
public sealed class AppServices
{
    private readonly Dispatcher _dispatcher;
    private readonly DialogProxy _dialogs = new();
    private readonly ToastProxy _toasts = new();
    private ForgeViewModel? _forge;
    private CompareViewModel? _compare;

    private AppServices(CommandLineOptions options, Dispatcher dispatcher, SingleInstance? instance, PreloadedSettings preloaded)
    {
        Options = options;
        _dispatcher = dispatcher;
        Instance = instance;
        Paths = preloaded.Paths;
        SettingsStore = preloaded.Store;
        Settings = preloaded.Settings;
        History = new HistoryStore(Paths);
        Ui = new WpfUiDispatcher(dispatcher);
        Profiles = new ProfileCatalog(Settings, _ => Persist());
        Engine = new DeferredBatchProcessor();
        Batch = new TrackingBatchProcessor(Engine, History);
        Watch = new WatchFolderService(Batch, Profiles);
        Integration = new ShellIntegrationService();
        Sound = new SoundService(() => Settings.PlayChime);
        Thumbnails = new ThumbnailService();
        ShellLauncher = new ShellLauncher();
        Clipboard = new ClipboardService();
        Tools = new ToolCatalog(this);
        Comparison = new ComparisonLoader(this);
        Shell = new ShellViewModel(CreatePage);
        Palette = new CommandPaletteViewModel(PaletteCommands);
        Watch.WatchedJobCompleted += OnWatchedJobCompleted;
    }

    public CommandLineOptions Options { get; }

    public DataPaths Paths { get; }

    public AppSettings Settings { get; }

    public SettingsStore SettingsStore { get; }

    public HistoryStore History { get; }

    public IUiDispatcher Ui { get; }

    public ProfileCatalog Profiles { get; }

    /// <summary>The engine slot: usable immediately, connected to the real scheduler once it is ready.</summary>
    public DeferredBatchProcessor Engine { get; }

    /// <summary>What the UI talks to (records history and profile ids).</summary>
    public IBatchProcessor Batch { get; }

    public WatchFolderService Watch { get; }

    public ShellIntegrationService Integration { get; }

    public SoundService Sound { get; }

    public ThumbnailService Thumbnails { get; }

    public ShellLauncher ShellLauncher { get; }

    public ClipboardService Clipboard { get; }

    public ToolCatalog Tools { get; }

    public ComparisonLoader Comparison { get; }

    public ShellViewModel Shell { get; }

    public CommandPaletteViewModel Palette { get; }

    public SingleInstance? Instance { get; }

    /// <summary>Stable dialog service; forwards to the window's ModalHost once <see cref="AttachDialogs"/> ran.</summary>
    public IDialogService Dialogs => _dialogs;

    /// <summary>Stable toast service; forwards to the window's ToastHost once <see cref="AttachToasts"/> ran.</summary>
    public IToastService Toasts => _toasts;

    public void AttachDialogs(IDialogService real) => _dialogs.Target = real;

    public void AttachToasts(IToastService real) => _toasts.Target = real;

    /// <summary>This process's private temp folder (null until background start).</summary>
    public string? SessionTempDir { get; private set; }

    /// <summary>The Forge page (created eagerly: it receives files from every entry point).</summary>
    public ForgeViewModel Forge => _forge ??= CreateForge();

    public CompareViewModel Compare => _compare ??= new CompareViewModel(Comparison);

    /// <summary>Raised on the UI thread when a second launch asks this window to come forward.</summary>
    public event EventHandler? ActivateRequested;

    /// <summary>Raised on the UI thread when the user picks "replay intro" in the palette.</summary>
    public event EventHandler? ReplayIntroRequested;

    public static AppServices Create(CommandLineOptions options, Dispatcher dispatcher, SingleInstance? instance, PreloadedSettings preloaded) =>
        new(options, dispatcher, instance, preloaded);

    /// <summary>
    /// Resolves the data folder and reads settings on a worker thread (started from Main, before WPF initialises):
    /// the JSON metadata warm-up costs ~65 ms of JIT that now overlaps the WPF start instead of delaying the window.
    /// </summary>
    public static Task<PreloadedSettings> PreloadAsync(CommandLineOptions options) => Task.Run(() =>
    {
        var paths = DataPaths.Resolve(options.DataDir, AppContext.BaseDirectory).EnsureCreated();
        var store = new SettingsStore(paths);
        return new PreloadedSettings(paths, store, store.Load());
    });

    /// <summary>Saves settings soon (debounced, atomic).</summary>
    public void Persist() => SettingsStore.SaveDebounced(Settings);

    /// <summary>Starts background services after the first frame. Never awaited by the UI.</summary>
    public void StartBackground() => _ = Task.Run(InitializeAsync);

    /// <summary>Queues command-line files, applies the profile and optionally starts.</summary>
    public async Task HandleCommandLineAsync(CommandLineOptions options)
    {
        if (options.Profile is { } profile && Profiles.Find(profile) is not null)
        {
            Profiles.SetActive(profile);
        }

        if (options.Files.Count > 0)
        {
            Shell.Navigate(PageKind.Forge);
            int added = await Forge.AddPathsAsync(options.Files).ConfigureAwait(true);
            if (added > 0 && options.Start && Forge.StartCommand.CanExecute(null))
            {
                Forge.StartCommand.Execute(null);
            }
        }
    }

    /// <summary>Queues clipboard files or a pasted bitmap (UI thread).</summary>
    public async Task PasteAsync()
    {
        var paths = Clipboard.GetPaths();
        if (paths.Count == 0)
        {
            Toasts.Show(Loc.Get("L.Forge.Paste"), Loc.Get("L.Toast.ClipboardEmpty"), ToastKind.Info);
            return;
        }

        Shell.Navigate(PageKind.Forge);
        int added = await Forge.AddPathsAsync(paths).ConfigureAwait(true);
        if (added > 0)
        {
            Toasts.Show(Loc.Get("L.Nav.Forge"), Loc.Format("L.Toast.Added", added), ToastKind.Info);
        }
    }

    /// <summary>Fast shutdown: cancel work, kill encoders (job object), flush settings, wipe temp in the background.</summary>
    public void Shutdown()
    {
        StartupProfiler.Mark("shutdown");
        try
        {
            Batch.CancelAll();
            EngineBootstrap.Shutdown();
            Watch.Dispose();
            SettingsStore.Flush();
            Sound.Dispose();
            Instance?.Dispose();
            if (SessionTempDir is { } temp)
            {
                Task.Run(() => SessionTemp.Delete(temp)).Wait(TimeSpan.FromMilliseconds(150));
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or AggregateException)
        {
            Log.Error("Shutdown step failed.", e);
        }
        finally
        {
            Log.Info("Shutdown.");
            Log.Flush(TimeSpan.FromMilliseconds(120));
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            Log.Init(Paths.LogsDir);
            Log.Info($"Carbonado {typeof(AppServices).Assembly.GetName().Version} starting (data: {Paths.Root}, portable: {Paths.IsPortable}).");
            SessionTemp.CleanupStale(Paths.TempRoot, TimeSpan.FromDays(3));
            SessionTempDir = SessionTemp.Create(Paths.TempRoot);
            await Tools.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            EngineBootstrap.TryAttach(this);
            Watch.Apply(Settings.WatchRules);
            if (Instance is { IsPrimary: true } instance)
            {
                instance.ArgumentsReceived += OnArgumentsForwarded;
                instance.Listen();
            }

            if (!Settings.LegacyScanDone)
            {
                await OfferLegacyMigrationAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Background start failed.", e);
        }
    }

    private async Task OfferLegacyMigrationAsync()
    {
        var scan = await Task.Run(() => new LegacyMigrator().Scan(LegacyRoots())).ConfigureAwait(false);
        if (!scan.FoundAnything)
        {
            Settings.LegacyScanDone = true;
            Persist();
            return;
        }

        Ui.Post(async () =>
        {
            var answer = await Dialogs.ConfirmAsync(Loc.Get("L.FirstRun.Legacy.Title"), Loc.Get("L.FirstRun.Legacy.Message"),
                Loc.Get("L.FirstRun.Legacy.Open"), Loc.Get("L.FirstRun.Legacy.Later")).ConfigureAwait(true);
            Settings.LegacyScanDone = true;
            Persist();
            if (answer == ConfirmResult.Primary)
            {
                Shell.Navigate(PageKind.Settings);
                if (Shell.PageFor(PageKind.Settings) is SettingsViewModel settings)
                {
                    await settings.ScanLegacyCommand.ExecuteAsync(null).ConfigureAwait(true);
                }
            }
        });
    }

    /// <summary>Folders searched for IMGCompress 6: next to Carbonado, its parent, the working folder and common user folders.</summary>
    public static IEnumerable<string> LegacyRoots()
    {
        string app = AppContext.BaseDirectory;
        yield return app;
        if (Directory.GetParent(app.TrimEnd(Path.DirectorySeparatorChar))?.FullName is { } parent)
        {
            yield return parent;
        }

        yield return Environment.CurrentDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void OnArgumentsForwarded(object? sender, string[] args) => Ui.Post(async () =>
    {
        ActivateRequested?.Invoke(this, EventArgs.Empty);
        var options = CommandLine.Parse(args);
        await HandleCommandLineAsync(options).ConfigureAwait(true);
    });

    private void OnWatchedJobCompleted(object? sender, CompressionResult result)
    {
        if (result.Outcome == JobOutcome.Compressed)
        {
            Toasts.Show(Loc.Get("L.Toast.Watch.Title"), Loc.Format("L.Toast.Watch.Message", Path.GetFileName(result.InputPath), FormatText.Percent(result.SavedFraction)), ToastKind.Success);
        }
    }

    private ForgeViewModel CreateForge()
    {
        var forge = new ForgeViewModel(Batch, Profiles, () => Settings.Output, Thumbnails, Ui, Dialogs, ShellLauncher, Toasts, Sound)
        {
            PasteCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(PasteAsync),
        };
        forge.CompareRequested += (_, item) =>
        {
            if (item.OutputPath is { } output)
            {
                Compare.Show(new ComparisonSource(item.Path, output, item.Result));
                Shell.Navigate(PageKind.Compare);
            }
        };
        return forge;
    }

    private object CreatePage(PageKind kind) => kind switch
    {
        PageKind.Forge => Forge,
        PageKind.Compare => Compare,
        PageKind.Profiles => new ProfilesViewModel(Profiles, Dialogs),
        PageKind.Archive => CreateArchive(),
        PageKind.Engines => new EnginesViewModel(Tools, ShellLauncher, Ui),
        PageKind.Settings => new SettingsViewModel(new SettingsContext(
            Settings,
            _ =>
            {
                Persist();
                Forge.RefreshDestination();
            },
            Dialogs,
            ShellLauncher,
            Profiles,
            Paths,
            History,
            Integration,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Carbonado.exe"),
            rules => Watch.Apply(rules),
            ct => Task.Run(() => new LegacyMigrator().Scan(LegacyRoots()), ct),
            ApplyMigration,
            language => Loc.Apply(Application.Current, language),
            MotionSettings.SetReducedMotion)),
        PageKind.About => new AboutViewModel(ShellLauncher),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private ArchiveViewModel CreateArchive()
    {
        var archive = new ArchiveViewModel(History, Ui, Dialogs, ShellLauncher);
        _ = archive.RefreshAsync();
        return archive;
    }

    private MigrationReport ApplyMigration(LegacyScanResult scan, MigrationOptions options)
    {
        var report = new LegacyMigrator().Apply(scan, options, Settings, paths => Ui.Post(() => _ = Forge.AddPathsAsync(paths)), RecycleBin.Instance);
        Persist();
        if (report.ToolsRegistered > 0)
        {
            _ = Tools.RefreshAsync(CancellationToken.None);
        }

        return report;
    }

    private IEnumerable<PaletteCommand> PaletteCommands()
    {
        var forge = Forge;
        yield return new PaletteCommand("L.Cmd.AddFiles", "Icon.AddFile", "Ctrl+O", () => _ = forge.AddFilesCommand.ExecuteAsync(null));
        yield return new PaletteCommand("L.Cmd.AddFolder", "Icon.AddFolder", "Ctrl+Shift+O", () => _ = forge.AddFolderCommand.ExecuteAsync(null));
        yield return new PaletteCommand("L.Cmd.Paste", "Icon.Paste", "Ctrl+V", () => _ = PasteAsync(), () => Clipboard.HasContent());
        yield return new PaletteCommand("L.Cmd.Compress", "Icon.Compress", "Ctrl+Enter", () => forge.StartCommand.Execute(null), () => forge.StartCommand.CanExecute(null));
        yield return new PaletteCommand("L.Cmd.Pause", "Icon.Pause", null, () => forge.PauseCommand.Execute(null), () => forge.PauseCommand.CanExecute(null));
        yield return new PaletteCommand("L.Cmd.Resume", "Icon.Play", null, () => forge.ResumeCommand.Execute(null), () => forge.ResumeCommand.CanExecute(null));
        yield return new PaletteCommand("L.Cmd.Cancel", "Icon.Stop", null, () => forge.CancelCommand.Execute(null), () => forge.CancelCommand.CanExecute(null));
        yield return new PaletteCommand("L.Cmd.Clear", "Icon.Remove", null, () => _ = forge.ClearCommand.ExecuteAsync(null), () => forge.HasItems);
        foreach (var entry in Shell.Navigation)
        {
            yield return new PaletteCommand(Loc.Format("L.Cmd.GoTo", entry.Label), entry.IconKey, entry.Shortcut, () => Shell.Navigate(entry.Kind), Literal: true);
        }

        foreach (var profile in Profiles.All)
        {
            yield return new PaletteCommand(Loc.Format("L.Cmd.UseProfile", profile.DisplayName(Loc.Language)), "Icon.Diamond", null,
                () => Profiles.SetActive(profile.Id), Literal: true);
        }

        yield return new PaletteCommand(Loc.Format("L.Cmd.Language", "Italiano"), "Icon.Language", null, () => SetLanguage("it"), Literal: true);
        yield return new PaletteCommand(Loc.Format("L.Cmd.Language", "English"), "Icon.Language", null, () => SetLanguage("en"), Literal: true);
        yield return new PaletteCommand("L.Cmd.ToggleMotion", "Icon.Motion", null, () =>
        {
            Settings.ReducedMotion = !MotionSettings.ReducedMotion;
            MotionSettings.SetReducedMotion(Settings.ReducedMotion);
            Persist();
        });
        yield return new PaletteCommand("L.Cmd.OpenData", "Icon.Data", null, () => ShellLauncher.OpenFolder(Paths.Root));
        yield return new PaletteCommand("L.Cmd.ReplayIntro", "Icon.Sparkle", null, () => ReplayIntroRequested?.Invoke(this, EventArgs.Empty));
    }

    private void SetLanguage(string language)
    {
        Settings.Language = language;
        Loc.Apply(Application.Current, language);
        Persist();
    }

    /// <summary>What <see cref="PreloadAsync"/> produced.</summary>
    public sealed record PreloadedSettings(DataPaths Paths, SettingsStore Store, AppSettings Settings);

    private sealed class DialogProxy : IDialogService
    {
        public IDialogService? Target { get; set; }

        public Task<ConfirmResult> ConfirmAsync(string title, string message, string primary, string? secondary = null, bool danger = false) =>
            Target?.ConfirmAsync(title, message, primary, secondary, danger) ?? Task.FromResult(ConfirmResult.Cancel);

        public IReadOnlyList<string> PickFiles() => Target?.PickFiles() ?? [];

        public string? PickFolder(string? initial = null) => Target?.PickFolder(initial);
    }

    private sealed class ToastProxy : IToastService
    {
        public IToastService? Target { get; set; }

        public void Show(string title, string message, ToastKind kind = ToastKind.Info)
        {
            if (Target is { } target)
            {
                target.Show(title, message, kind);
            }
            else
            {
                Log.Info($"[toast] {title}: {message}");
            }
        }
    }
}
