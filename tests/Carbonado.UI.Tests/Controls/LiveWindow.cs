using System.Diagnostics;
using System.Windows;
using Carbonado.UI.Tests.Theme;

namespace Carbonado.UI.Tests.Controls;

/// <summary>
/// A real, themed, off-screen window (never activated, no taskbar entry) so controls are Loaded and animations
/// tick. Use inside <see cref="TestSupport.Sta.RunLoop"/>.
/// </summary>
internal sealed class LiveWindow : IDisposable
{
    public LiveWindow(FrameworkElement content, double width = 480, double height = 320)
    {
        Window = new Window
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = content,
        };
        Window.Resources.MergedDictionaries.Add(ThemeHarness.Theme);
        Window.Show();
    }

    public Window Window { get; }

    /// <summary>Polls <paramref name="condition"/> on the dispatcher until it holds or the timeout elapses.</summary>
    public static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(4);
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > limit)
            {
                throw new TimeoutException("Condition not reached in time.");
            }

            await Task.Delay(15);
        }
    }

    public void Dispose() => Window.Close();
}
