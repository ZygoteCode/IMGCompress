using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
using Carbonado.App.Tests.TestSupport;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Migration;
using Carbonado.Core.Persistence;
using Carbonado.Core.Tools;
using Microsoft.Win32;

namespace Carbonado.App.Tests.Pages;

[CollectionDefinition("WpfRender", DisableParallelization = true)]
public sealed class WpfRenderCollection;

/// <summary>
/// Renders every shell page offscreen in both languages and fails on XAML load errors, WPF binding errors,
/// unresolved localization keys (visible text starting with "L.") and blank output.
/// </summary>
[Collection("WpfRender")]
public sealed class PageRenderTests
{
    private const int Width = 1240;
    private const int Height = 780;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (string page in new[] { "Forge", "Compare", "Profiles", "Archive", "Engines", "Settings", "About", "ConfirmDialog", "CommandPalette" })
        {
            foreach (string lang in new[] { "it", "en" })
            {
                data.Add(page, lang);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Page_RendersCleanly(string page, string language)
    {
        Sta.Invoke(() =>
        {
            var app = Sta.App;
            Loc.Apply(app, language);
            using var dir = new TempFolder();
            var errors = new List<string>();
            var listener = new CollectingListener(errors);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            Window? window = null;
            try
            {
                var view = Build(page, dir, out var disposables);
                using var disposer = new Disposer(disposables);
                window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -20000,
                    Top = -20000,
                    Width = Width,
                    Height = Height,
                    Content = view,
                };
                window.SetResourceReference(Control.BackgroundProperty, "B.Bg");
                window.Show();
                window.UpdateLayout();
                Sta.Idle();
                window.UpdateLayout();

                var unresolved = new List<string>();
                CollectUnresolved(window, unresolved);
                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);

                Assert.True(errors.Count == 0, $"{page}/{language} binding errors:\n" + string.Join("\n", errors));
                Assert.True(unresolved.Count == 0, $"{page}/{language} unresolved keys: " + string.Join(", ", unresolved.Distinct()));
                Assert.False(IsBlank(bitmap), $"{page}/{language} rendered blank.");
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                window?.Close();
                Loc.Apply(app, "en");
            }
        });
    }

    private static FrameworkElement Build(string page, TempFolder dir, out List<IDisposable> disposables)
    {
        disposables = [];
        var settings = new AppSettings();
        var catalog = new ProfileCatalog(settings, _ => { });
        var paths = DataPaths.Resolve(dir.Path, AppContext.BaseDirectory);
        switch (page)
        {
            case "Forge":
                {
                    var vm = new ForgeViewModel(new FakeBatch(), catalog, () => settings.Output, new FakeThumbnails(), new ImmediateDispatcher(),
                        new FakeDialogs(), new FakeShell(), new FakeToasts(), new FakeSound());
                    disposables.Add(vm);
                    Task.Run(() => vm.AddPathsAsync([dir.Touch("a.png", 1000), dir.Touch("b.jpg", 2000)])).GetAwaiter().GetResult();
                    if (vm.Items.Count > 1)
                    {
                        var done = vm.Items[1];
                        done.Status = QueueItemStatus.Done;
                        done.OutputBytes = 400;
                    }

                    return new ForgeView { DataContext = vm };
                }

            case "Compare":
                return new CompareView { DataContext = new CompareViewModel(new NullLoader()) };
            case "Profiles":
                return new ProfilesView { DataContext = new ProfilesViewModel(catalog, new FakeDialogs()) };
            case "Archive":
                {
                    var history = new HistoryStore(paths.EnsureCreated());
                    history.Append(Result("x.png", 1000, 250, OutputFormat.Avif), "vs");
                    history.Append(Result("y.png", 1000, 500, OutputFormat.Jxl), "vs");
                    var vm = new ArchiveViewModel(history, new ImmediateDispatcher(), new FakeDialogs(), new FakeShell());
                    Task.Run(vm.RefreshAsync).GetAwaiter().GetResult();
                    return new ArchiveView { DataContext = vm };
                }

            case "Engines":
                {
                    var tools = new FakeToolCatalog
                    {
                        Tools =
                        [
                            new ToolInfo(ToolId.Cjxl, @"C:\t\cjxl.exe", "libjxl", "0.13.0", ToolIntegrity.Verified, "aa"),
                            new ToolInfo(ToolId.Avifenc, @"C:\t\avifenc.exe", "libavif", "1.4.2", ToolIntegrity.Mismatch, "cc"),
                        ],
                    };
                    return new EnginesView { DataContext = new EnginesViewModel(tools, new FakeShell(), new ImmediateDispatcher()) };
                }

            case "Settings":
                {
                    var ctx = new SettingsContext(settings, _ => { }, new FakeDialogs(), new FakeShell(), catalog, paths,
                        new HistoryStore(paths), new ShellIntegrationService(Registry.CurrentUser, @"Software\Carbonado.Tests\" + Guid.NewGuid().ToString("N")),
                        @"C:\Carbonado\Carbonado.exe", _ => { }, _ => Task.FromResult(LegacyScanResult.Empty),
                        (_, _) => throw new InvalidOperationException(), _ => { }, _ => { });
                    return new SettingsView { DataContext = new SettingsViewModel(ctx) };
                }

            case "About":
                return new AboutView { DataContext = new AboutViewModel(new FakeShell()) };
            case "ConfirmDialog":
                return new ConfirmDialog("Title", "Message body", "OK", "Cancel", danger: true);
            case "CommandPalette":
                {
                    var vm = new CommandPaletteViewModel(() =>
                    [
                        new PaletteCommand("Compress", "Icon.Play", "Ctrl+Enter", () => { }, Literal: true),
                        new PaletteCommand("Clear the queue", "Icon.Trash", null, () => { }, Literal: true),
                    ]);
                    vm.Refresh();
                    return new CommandPaletteView { DataContext = vm };
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(page));
        }
    }

    private static void CollectUnresolved(DependencyObject node, List<string> found)
    {
        switch (node)
        {
            case TextBlock { Text: { } t } when IsKey(t):
                found.Add(t);
                break;
            case AccessText { Text: { } a } when IsKey(a):
                found.Add(a);
                break;
            case ContentPresenter { Content: string c } when IsKey(c):
                found.Add(c);
                break;
        }

        if (node is Visual)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                CollectUnresolved(VisualTreeHelper.GetChild(node, i), found);
            }
        }
    }

    private static bool IsKey(string text) => text.StartsWith("L.", StringComparison.Ordinal);

    private static bool IsBlank(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (int i = 4; i < pixels.Length; i += 4)
        {
            if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2] || pixels[i + 3] != pixels[3])
            {
                return false;
            }
        }

        return true;
    }

    private static CompressionResult Result(string input, long original, long output, OutputFormat format) =>
        new(Guid.NewGuid(), input, input + ".out", JobOutcome.Compressed, original, output, format, "t", 90, VerificationKind.PerceptualScore, TimeSpan.FromSeconds(1), [], null, 1, 1);

    private sealed class CollectingListener(List<string> sink) : TraceListener
    {
        public override void Write(string? message) => Add(message);

        public override void WriteLine(string? message) => Add(message);

        private void Add(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                lock (sink)
                {
                    sink.Add(message.Trim());
                }
            }
        }
    }

    private sealed class Disposer(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    private sealed class NullLoader : IComparisonLoader
    {
        public Task<ComparisonImages> LoadAsync(string original, string output, CancellationToken ct) => throw new NotSupportedException();

        public Task<(ImageSource Map, double Score)> HeatmapAsync(string original, string output, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeToolCatalog : IToolCatalog
    {
        public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

        public string ToolsFolder => @"C:\t";

        public IReadOnlyCollection<OutputFormat> AvailableFormats { get; } = [OutputFormat.Jxl];

        public event EventHandler? Changed;

        public Task RefreshAsync(CancellationToken ct)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    /// <summary>One shared STA thread with a dispatcher and a single Application for the whole process.</summary>
    private static class Sta
    {
        private static readonly Lazy<Dispatcher> s_dispatcher = new(Start);
        private static Application? s_app;

        public static Application App => s_app!;

        public static void Invoke(Action action)
        {
            ExceptionDispatchInfo? error = null;
            s_dispatcher.Value.Invoke(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
            });
            error?.Throw();
        }

        public static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static Dispatcher Start()
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
                s_app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                s_app.Resources.MergedDictionaries.Add(new Carbonado.UI.Theme.SharedDictionary
                {
                    Source = new Uri("pack://application:,,,/Carbonado;component/Shell/AppStyles.xaml", UriKind.Absolute),
                });
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "PageRenderTests STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return dispatcher!;
        }
    }
}
