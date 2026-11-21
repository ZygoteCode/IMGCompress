using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Carbonado.UI.Motion;

/// <summary>
/// Gate for decorative, never-ending motion (ambient dust, idle shimmer). It runs only while someone is there to see
/// it: the application is the active one and there was input within <see cref="IdleAfter"/>. Everything else —
/// meaningful transitions, progress, the intro — ignores this gate. Measured reason: on a 199 Hz display an
/// always-on dust field kept one core at ~80 % while the app sat idle.
/// </summary>
public static class AmbientMotion
{
    private static bool s_attached;
    private static bool s_appActive = true;
    private static bool s_idle;
    private static long s_lastInput;
    private static DispatcherTimer? s_idleTimer;

    /// <summary>Raised on the UI thread when <see cref="IsActive"/> changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>Time without keyboard/mouse/touch input after which ambient motion rests (default 20 s).</summary>
    public static TimeSpan IdleAfter { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>True while ambient motion may run (app active and not idle). Always true until attached.</summary>
    public static bool IsActive => s_appActive && !s_idle;

    /// <summary>
    /// Hooks application activation and input on the current (UI) thread; idempotent. Controls call it when they
    /// load, so the gate costs nothing in apps or tests that never show ambient motion.
    /// </summary>
    public static void EnsureAttached()
    {
        if (s_attached || Application.Current is not { } app || !app.CheckAccess())
        {
            return;
        }

        s_attached = true;
        s_appActive = app.Windows.Count == 0 || app.Windows.Cast<Window>().Any(w => w.IsActive);
        s_lastInput = Environment.TickCount64;
        app.Activated += (_, _) => SetAppActive(true);
        app.Deactivated += (_, _) => SetAppActive(false);
        InputManager.Current.PreNotifyInput += (_, _) => OnInput();
        s_idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        s_idleTimer.Tick += (_, _) => CheckIdle();
        s_idleTimer.Start();
        Raise();
    }

    private static void OnInput()
    {
        s_lastInput = Environment.TickCount64;
        if (s_idle)
        {
            s_idle = false;
            Raise();
        }
    }

    private static void CheckIdle()
    {
        bool idle = Environment.TickCount64 - s_lastInput >= (long)IdleAfter.TotalMilliseconds;
        if (idle != s_idle)
        {
            s_idle = idle;
            Raise();
        }
    }

    private static void SetAppActive(bool active)
    {
        if (active)
        {
            s_lastInput = Environment.TickCount64;
            s_idle = false;
        }

        if (active != s_appActive)
        {
            s_appActive = active;
        }

        Raise();
    }

    private static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
}
