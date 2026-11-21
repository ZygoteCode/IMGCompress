using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Carbonado.UI.Motion;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Stack of <see cref="Toast"/> notifications (place it bottom-right over the shell). <see cref="Show"/> slides a
/// toast in from the right, dismisses it after <see cref="DismissAfter"/> (paused while hovered) and keeps at most
/// <see cref="MaxToasts"/>; closing fades it out with a short drift. Bound items get a <see cref="Toast"/> container.
/// </summary>
public class ToastHost : ItemsControl
{
    public static readonly DependencyProperty DismissAfterProperty = DependencyProperty.Register(
        nameof(DismissAfter), typeof(TimeSpan), typeof(ToastHost), new FrameworkPropertyMetadata(TimeSpan.FromSeconds(5)));

    public static readonly DependencyProperty MaxToastsProperty = DependencyProperty.Register(
        nameof(MaxToasts), typeof(int), typeof(ToastHost), new FrameworkPropertyMetadata(4), v => v is int n && n > 0);

    private readonly Dictionary<Toast, DispatcherTimer> _timers = [];
    private readonly HashSet<Toast> _leaving = [];

    public ToastHost()
    {
        AddHandler(Toast.CloseRequestedEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Toast toast)
            {
                Dismiss(toast);
            }
        }));
    }

    /// <summary>How long a toast stays before leaving on its own; <see cref="TimeSpan.Zero"/> keeps it.</summary>
    public TimeSpan DismissAfter
    {
        get => (TimeSpan)GetValue(DismissAfterProperty);
        set => SetValue(DismissAfterProperty, value);
    }

    /// <summary>Visible toasts; the oldest leaves when a new one exceeds it.</summary>
    public int MaxToasts
    {
        get => (int)GetValue(MaxToastsProperty);
        set => SetValue(MaxToastsProperty, value);
    }

    /// <summary>Adds a toast with the given (localized) texts and plays its entrance.</summary>
    public Toast Show(string title, string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new Toast { Title = title, Message = message, Kind = kind };
        Items.Add(toast);
        Enter(toast);
        Schedule(toast);
        var staying = Items.OfType<Toast>().Where(t => !_leaving.Contains(t)).ToList();
        foreach (var old in staying.Take(Math.Max(0, staying.Count - MaxToasts)))
        {
            Dismiss(old);
        }

        return toast;
    }

    /// <summary>Plays the exit and removes <paramref name="toast"/>.</summary>
    public void Dismiss(Toast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (!_leaving.Add(toast))
        {
            return;
        }

        if (_timers.Remove(toast, out var timer))
        {
            timer.Stop();
        }

        bool reduced = MotionSettings.ReducedMotion;
        var fade = new DoubleAnimation(0, MotionSettings.Scale(TimeSpan.FromMilliseconds(180))) { EasingFunction = Eases.Glide };
        fade.Completed += (_, _) =>
        {
            _leaving.Remove(toast);
            Items.Remove(toast);
        };
        toast.BeginAnimation(OpacityProperty, fade);
        if (!reduced && toast.RenderTransform is TranslateTransform shift)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, Tween(shift.X, 16, TimeSpan.FromMilliseconds(180), Eases.Glide));
        }
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is Toast;

    protected override DependencyObject GetContainerForItemOverride() => new Toast();

    private static void Enter(Toast toast)
    {
        var shift = new TranslateTransform();
        toast.RenderTransform = shift;
        toast.BeginAnimation(OpacityProperty, Tween(0, 1, TimeSpan.FromMilliseconds(240), Eases.Glide));
        if (!MotionSettings.ReducedMotion)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, Tween(28, 0, TimeSpan.FromMilliseconds(420), Eases.Crystallize));
        }
    }

    private void Schedule(Toast toast)
    {
        if (DismissAfter <= TimeSpan.Zero)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = DismissAfter };
        timer.Tick += (_, _) => Dismiss(toast);
        _timers[toast] = timer;
        toast.MouseEnter += (_, _) => timer.Stop();
        toast.MouseLeave += (_, _) =>
        {
            if (!_leaving.Contains(toast))
            {
                timer.Start();
            }
        };
        timer.Start();
    }

    private static DoubleAnimation Tween(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(from, to, MotionSettings.Scale(duration)) { EasingFunction = ease };
        animation.Freeze();
        return animation;
    }
}
