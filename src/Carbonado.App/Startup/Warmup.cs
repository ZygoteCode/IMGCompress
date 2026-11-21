using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Carbonado.App.Startup;

/// <summary>
/// Startup overlap helpers, measured with an EventPipe sampling trace of the published build:
/// <list type="bullet">
/// <item>WPF's first composition channel waits ~120 ms (cold) for the process-wide render engine to start (render
/// thread, Direct3D). A helper thread opens one from Main, so the engine boots while the UI thread runs the
/// Application constructor; the UI thread's own channel then connects in a few milliseconds.</item>
/// <item>The first text layout loads the Inter faces and line services: a short-lived thread does it in advance.</item>
/// </list>
/// </summary>
internal static class Warmup
{
    private const string FontUri = "pack://application:,,,/Carbonado.UI;component/Assets/Fonts/#Inter";
    private static Dispatcher? s_renderHelper;
    private static int s_released;

    /// <summary>Main (primary instance only): boots the render engine on a helper thread.</summary>
    public static void StartRenderEngine()
    {
        var thread = new Thread(RunRenderHelper) { IsBackground = true, Name = "Carbonado render warm-up" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// UI thread: opens this dispatcher's composition channel now (fast once the helper booted the engine; otherwise
    /// the wait overlaps the settings, font and intro work running in the background) and releases the helper.
    /// </summary>
    public static void ConnectRenderThread()
    {
        Connect();
        Interlocked.Exchange(ref s_released, 1);
        if (Volatile.Read(ref s_renderHelper) is { } helper)
        {
            helper.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    /// <summary>
    /// UI thread, before <see cref="ConnectRenderThread"/>: while the engine may still be booting, runs the static
    /// constructors and realizes the styles the window needs first; that work would otherwise happen inside the
    /// window's XAML after the wait. Same thread, same work, earlier: no ordering or threading change.
    /// </summary>
    public static void PrepareShellTypes(Func<object, object?> findResource)
    {
        foreach (var type in s_shellTypes)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        }

        foreach (object key in s_shellStyles)
        {
            _ = findResource(key);
        }
    }

    private static readonly Type[] s_shellTypes =
    [
        typeof(Window), typeof(System.Windows.Controls.ContentPresenter), typeof(System.Windows.Controls.ContentControl),
        typeof(System.Windows.Controls.Control), typeof(System.Windows.Controls.TextBlock), typeof(System.Windows.Controls.Border),
        typeof(System.Windows.Controls.Grid), typeof(System.Windows.Controls.StackPanel), typeof(System.Windows.Documents.AdornerDecorator),
        typeof(System.Windows.Shapes.Path), typeof(System.Windows.Shapes.Rectangle), typeof(System.Windows.Controls.Button),
        typeof(System.Windows.Controls.Primitives.ToggleButton), typeof(System.Windows.Controls.ListBox), typeof(System.Windows.Controls.ListBoxItem),
        typeof(System.Windows.Controls.ItemsControl), typeof(System.Windows.Controls.ScrollViewer), typeof(System.Windows.Controls.ToolTip),
        typeof(System.Windows.Controls.TextBox), typeof(System.Windows.Controls.Expander), typeof(System.Windows.Controls.AccessText),
        typeof(System.Windows.Documents.TextElement), typeof(System.Windows.Documents.Run), typeof(System.Windows.Controls.Validation),
        typeof(System.Windows.Input.KeyboardNavigation), typeof(System.Windows.Media.Animation.DoubleAnimation),
        typeof(System.Windows.Media.Animation.Storyboard), typeof(VisualStateManager), typeof(System.Windows.Shell.WindowChrome),
        typeof(Carbonado.UI.Controls.NavRail), typeof(Carbonado.UI.Controls.NavItem), typeof(Carbonado.UI.Brand.DiamondMark),
        typeof(Carbonado.UI.Controls.IconPresenter), typeof(Carbonado.UI.Controls.ModalHost), typeof(Carbonado.UI.Controls.ToastHost),
        typeof(Carbonado.UI.Transitions.TransitionHost),
    ];

    private static readonly object[] s_shellStyles =
    [
        typeof(Window), "Button.Caption", "Button.Ghost", "Text.Body", "Text.Caption", typeof(Carbonado.UI.Controls.NavRail),
        typeof(Carbonado.UI.Controls.NavItem), typeof(Carbonado.UI.Controls.ModalHost), typeof(Carbonado.UI.Controls.ToastHost),
        typeof(Carbonado.UI.Transitions.TransitionHost),
    ];

    /// <summary>Lays out a line in each Inter weight on a short-lived thread (font files, DirectWrite faces, line services).</summary>
    public static void StartFonts()
    {
        var thread = new Thread(WarmFonts) { IsBackground = true, Name = "Carbonado font warm-up" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Adding a child is the cheapest public call that makes WPF create the dispatcher's MediaContext.</summary>
    private static void Connect()
    {
        var host = new ContainerVisual();
        host.Children.Add(new DrawingVisual());
        host.Children.Clear();
    }

    private static void RunRenderHelper()
    {
        try
        {
            Connect();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            // Warm-up only: the UI thread starts the engine itself.
        }

        // Keep this context alive (and pump its messages) until the UI thread has its own channel: closing it first
        // would let the engine shut down again. Whichever side arrives second ends the helper.
        var dispatcher = Dispatcher.CurrentDispatcher;
        Interlocked.Exchange(ref s_renderHelper, dispatcher);
        if (Volatile.Read(ref s_released) == 1)
        {
            dispatcher.InvokeShutdown();
            return;
        }

        Dispatcher.Run();
    }

    private static void WarmFonts()
    {
        try
        {
            var family = new FontFamily(FontUri);
            foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold })
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                var text = new FormattedText("Carbonado 0123456789 Forgia — àèéìòù", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 13, Brushes.White, 1.0);
                _ = text.Width;
            }
        }
        catch (Exception e) when (e is IOException or UriFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Warm-up only: the UI thread loads the font on its own if this fails.
        }
        finally
        {
            // Text formatting created a dispatcher (and its hidden window) for this thread: release it.
            Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
    }
}
