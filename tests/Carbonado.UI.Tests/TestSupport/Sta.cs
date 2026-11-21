using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Carbonado.UI.Tests.TestSupport;

/// <summary>Runs test code on a fresh STA thread (WPF requirement), optionally with a running dispatcher loop.</summary>
public static class Sta
{
    /// <summary>Runs <paramref name="action"/> synchronously on a new STA thread and rethrows its exception.</summary>
    public static void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    /// <summary>
    /// Runs an async test body inside a dispatcher message loop on a new STA thread (needed for animations,
    /// CompositionTarget.Rendering and DispatcherTimer). Times out after <paramref name="timeout"/>.
    /// </summary>
    public static void RunLoop(Func<Task> body, TimeSpan? timeout = null)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var watchdog = new DispatcherTimer(timeout ?? TimeSpan.FromSeconds(30), DispatcherPriority.Send, (_, _) =>
            {
                error ??= ExceptionDispatchInfo.Capture(new TimeoutException("STA test body timed out."));
                dispatcher.InvokeShutdown();
            }, dispatcher);
            watchdog.Start();
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception e)
                {
                    error ??= ExceptionDispatchInfo.Capture(e);
                }
                finally
                {
                    watchdog.Stop();
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>Processes pending dispatcher work (layout, bindings) synchronously.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
