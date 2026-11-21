using System.Windows;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Theme;

/// <summary>How an element enters when it becomes visible.</summary>
public enum RevealMode
{
    /// <summary>No animation.</summary>
    None,

    /// <summary>Opacity only.</summary>
    Fade,

    /// <summary>Fade while rising 6 px into place.</summary>
    Rise,

    /// <summary>Fade while dropping 6 px into place (menus, drop-downs, tooltips).</summary>
    Drop,
}

/// <summary>
/// Attached <c>Reveal.Mode</c>: plays a short enter animation every time the element becomes visible (popups,
/// tooltips, expanded content). Honours reduced motion (fade only).
/// </summary>
public static class Reveal
{
    /// <summary>Distance of the rise/drop gesture in DIPs.</summary>
    public const double Offset = 6;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(RevealMode), typeof(Reveal), new PropertyMetadata(RevealMode.None, OnModeChanged));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(240);

    public static RevealMode GetMode(DependencyObject element) => (RevealMode)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, RevealMode value) => element.SetValue(ModeProperty, value);

    /// <summary>Plays the enter gesture of <paramref name="mode"/> on <paramref name="element"/> now.</summary>
    public static void Play(UIElement element, RevealMode mode)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (mode == RevealMode.None)
        {
            return;
        }

        double offset = mode switch
        {
            RevealMode.Rise => Offset,
            RevealMode.Drop => -Offset,
            _ => 0,
        };
        EnterAnimation.Play(element, offset, Duration, TimeSpan.Zero);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnIsVisibleChanged;
        if ((RevealMode)e.NewValue != RevealMode.None)
        {
            element.IsVisibleChanged += OnIsVisibleChanged;
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is UIElement element)
        {
            Play(element, GetMode(element));
        }
    }
}
