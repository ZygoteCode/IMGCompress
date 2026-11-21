using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Carbonado.App.Localization;
using Carbonado.App.Services;
using Carbonado.App.Startup;
using Carbonado.Core.Logging;
using Carbonado.Core.Persistence;
using Carbonado.UI.Brand;
using Carbonado.UI.Cinematics.Intro;
using Carbonado.UI.Transitions;

namespace Carbonado.App.Shell;

/// <summary>
/// The Carbonado window: custom chrome, navigation rail, page host, toasts, modal dialogs, command palette and the
/// cinematic intro. The window appears at once; the intro plays while services start in the background.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Uri s_pageTemplates = new("pack://application:,,,/Carbonado;component/Shell/PageTemplates.xaml", UriKind.Absolute);
    private readonly AppServices _services;
    private readonly CommandLineOptions _options;
    private IntroOverlay? _intro;
    private CommandPaletteView? _palette;
    private bool _firstFrame;
    private bool _closeConfirmed;
    private bool _confirmingClose;

    public MainWindow(AppServices services, CommandLineOptions options)
    {
        _services = services;
        _options = options;
        StartupProfiler.Mark("window-ctor");
        // Page templates (view-model → view) join before the XAML builds the tree, so adding them invalidates nothing.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = s_pageTemplates });
        InitializeComponent();
        StartupProfiler.Mark("window-xaml");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 52,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = default,
            UseAeroCaptionButtons = false,
        });
        AddKeyBindings(services.Shell);
        Wordmark.Data = WordmarkGeometry.Compose();
        DataContext = services.Shell;
        services.AttachDialogs(new DialogService(Modal, this));
        services.AttachToasts(new ToastService(Toasts, () => services.Settings.ShowToasts));
        services.ActivateRequested += (_, _) => BringToFront();
        services.ReplayIntroRequested += (_, _) => ReplayIntro();
        services.Palette.Executed += (_, _) => services.Shell.IsPaletteOpen = false;
        services.Shell.PropertyChanged += OnShellPropertyChanged;
        var mode = ResolveIntroMode(options.Intro, services.Settings.Intro);
        if (mode != IntroMode.Off)
        {
            CreateIntro(mode);
        }
        RestorePlacement(services.Settings.Window);
        StateChanged += (_, _) => UpdateMaximizeIcon();
        ContentRendered += OnContentRendered;
        PreviewKeyDown += OnPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
        Closing += OnClosing;
    }

    private void AddKeyBindings(ShellViewModel shell)
    {
        InputBindings.Add(new KeyBinding(shell.TogglePaletteCommand, Key.K, ModifierKeys.Control));
        Key[] digits = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7];
        foreach (var kind in Enum.GetValues<PageKind>())
        {
            InputBindings.Add(new KeyBinding(shell.NavigateCommand, digits[(int)kind], ModifierKeys.Control) { CommandParameter = kind });
        }
    }

    /// <summary>Maps the saved preference (and a command-line override) to the overlay mode.</summary>
    public static IntroMode ResolveIntroMode(IntroOverride commandLine, IntroPreference preference) => commandLine switch
    {
        IntroOverride.Cinematic => IntroMode.Cinematic,
        IntroOverride.Short => IntroMode.Short,
        IntroOverride.Off => IntroMode.Off,
        _ => preference switch
        {
            IntroPreference.Short => IntroMode.Short,
            IntroPreference.Off => IntroMode.Off,
            _ => IntroMode.Cinematic,
        },
    };

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Replays the intro (command palette).</summary>
    public void ReplayIntro()
    {
        var intro = _intro ?? CreateIntro(IntroMode.Cinematic);
        intro.Mode = IntroMode.Cinematic;
        intro.Visibility = Visibility.Visible;
        intro.Play();
    }

    /// <summary>The overlay exists only while an intro plays (Off mode never pays for it).</summary>
    private IntroOverlay CreateIntro(IntroMode mode)
    {
        _intro = new IntroOverlay { Mode = mode, LogoTarget = TitleLogo };
        AutomationProperties.SetAutomationId(_intro, "Intro.Overlay");
        Grid.SetRowSpan(_intro, 2);
        _intro.Completed += (_, _) =>
        {
            Root.Children.Remove(_intro);
            _intro = null;
        };
        Root.Children.Add(_intro);
        return _intro;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_firstFrame)
        {
            return;
        }

        _firstFrame = true;
        StartupProfiler.Mark("first-frame");
        // The page arrives right after the first frame and enters with its staggered reveal (hidden by the intro anyway).
        Pages.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ShellViewModel.CurrentPage)));
        if (_intro is { } intro)
        {
            intro.HandOffStarted += (_, _) => Stagger.Reveal(Body);
            intro.Play();
        }
        else
        {
            Stagger.Reveal(Body);
        }

        _services.StartBackground();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OnInteractive));
    }

    private async void OnInteractive()
    {
        StartupProfiler.Mark("interactive");
        StartupProfiler.WriteTo(Path.Combine(_services.Paths.LogsDir, "startup-last.txt"));
        Log.Info("Startup: " + string.Join(", ", StartupProfiler.Marks.Select(m => $"{m.Name}={m.Ms:F0}ms")));
        try
        {
            await _services.HandleCommandLineAsync(_options).ConfigureAwait(true);
            if (_options.SnapshotDir is { } snapshots)
            {
                await RenderSnapshotsAsync(snapshots).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Command line handling failed.", ex);
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsPaletteOpen) && _services.Shell.IsPaletteOpen)
        {
            if (_palette is null)
            {
                _palette = new CommandPaletteView
                {
                    DataContext = _services.Palette,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0),
                    Width = 620,
                };
                PaletteLayer.Children.Add(_palette);
            }

            _services.Palette.Refresh();
            _palette.FocusSearch();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _services.Shell.IsPaletteOpen)
        {
            _services.Shell.IsPaletteOpen = false;
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.O)
        {
            _ = shift ? _services.Forge.AddFolderCommand.ExecuteAsync(null) : _services.Forge.AddFilesCommand.ExecuteAsync(null);
            _services.Shell.Navigate(PageKind.Forge);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V)
        {
            _ = _services.PasteAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Enter && _services.Forge.StartCommand.CanExecute(null))
        {
            _services.Forge.StartCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                e.Handled = true;
                _services.Shell.Navigate(PageKind.Forge);
                await _services.Forge.AddPathsAsync(paths).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException)
        {
            Log.Warn($"Drop failed: {ex.Message}");
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        if (_closeConfirmed || !_services.Forge.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        if (_confirmingClose)
        {
            return;
        }

        _confirmingClose = true;
        try
        {
            var answer = await _services.Dialogs.ConfirmAsync(Loc.Get("L.Exit.Running.Title"), Loc.Get("L.Exit.Running.Message"),
                Loc.Get("L.Exit.Running.Confirm"), Loc.Get("L.Exit.Running.Stay"), danger: true).ConfigureAwait(true);
            if (answer == ConfirmResult.Primary)
            {
                _closeConfirmed = true;
                _services.Batch.CancelAll();
                Close();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPaletteScrimClick(object sender, MouseButtonEventArgs e) => _services.Shell.IsPaletteOpen = false;

    private void UpdateMaximizeIcon()
    {
        bool max = WindowState == WindowState.Maximized;
        MaximizeIcon.Icon = (Geometry)FindResource(max ? "Icon.Restore" : "Icon.Maximize");
        MaximizeButton.ToolTip = Loc.Get(max ? "L.Window.Restore" : "L.Window.Maximize");
        // A maximized borderless window overhangs the screen by the resize border: pad it back in.
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(bounds))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
        {
            _services.Settings.Window = new WindowPlacement(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized);
            _services.Persist();
        }
    }

    /// <summary>Visual regression mode: renders every page to PNG and closes.</summary>
    private async Task RenderSnapshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        _intro?.Skip();
        foreach (var kind in Enum.GetValues<PageKind>())
        {
            _services.Shell.Navigate(kind);
            await Task.Delay(900).ConfigureAwait(true);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var dpi = VisualTreeHelper.GetDpi(this);
            var target = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            target.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var fs = File.Create(Path.Combine(folder, $"page-{(int)kind + 1}-{kind}.png"));
            encoder.Save(fs);
        }

        _closeConfirmed = true;
        Close();
    }
}
