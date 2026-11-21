using System.Windows;
using System.Windows.Threading;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.App.Shell;
using Carbonado.App.Startup;
using Carbonado.Core.Logging;
using Carbonado.UI.Cinematics.Intro;
using Carbonado.UI.Motion;

namespace Carbonado.App;

/// <summary>
/// Code-only application (no App.xaml BAML on the startup path). Order matters for perceived speed: settings are
/// already loading on a worker thread (Main); intro assets and fonts warm up in the background while the UI thread
/// connects to the render thread; then theme + strings and the window. Every heavy service starts after the first frame.
/// </summary>
public sealed class App : Application
{
    private static readonly Uri s_styles = new("pack://application:,,,/Carbonado;component/Shell/AppStyles.xaml", UriKind.Absolute);
    private readonly CommandLineOptions _options;
    private readonly SingleInstance? _instance;
    private readonly Task<AppServices.PreloadedSettings> _preload;
    private AppServices? _services;

    public App(CommandLineOptions options, SingleInstance? instance, Task<AppServices.PreloadedSettings> preload)
    {
        _options = options;
        _instance = instance;
        _preload = preload;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    public AppServices Services => _services ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupProfiler.Mark("app-startup");
        if (_options.Intro != IntroOverride.Off)
        {
            IntroOverlay.PrepareAssets();
        }

        Warmup.StartFonts();
        Carbonado.UI.Brand.DiamondMark.PrepareInBackground();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        _services = AppServices.Create(_options, Dispatcher, _instance, _preload.GetAwaiter().GetResult());
        StartupProfiler.Mark("settings");
        Resources.MergedDictionaries.Add(new Carbonado.UI.Theme.SharedDictionary { Source = s_styles });
        Loc.Apply(this, _options.Language ?? _services.Settings.Language);
        MotionSettings.SetReducedMotion(_services.Settings.ReducedMotion);
        StartupProfiler.Mark("resources");

        // Everything above runs while the render engine boots on the helper thread started in Main; the window needs
        // this dispatcher's composition channel, which connects as soon as the engine is up.
        Warmup.PrepareShellTypes(TryFindResource);
        Warmup.ConnectRenderThread();
        StartupProfiler.Mark("render-thread");


        var window = new MainWindow(_services, _options);
        MainWindow = window;
        window.Show();
        StartupProfiler.Mark("window-shown");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Ruling (ledger): a fault in a cosmetic layer (intro frame, animation) must never take the app down.
        Log.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        _services?.Toasts.Show(Loc.Get("L.Error.Unexpected.Title"), e.Exception.Message, ToastKind.Error);
    }
}
