using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Carbonado.UI.Controls;

/// <summary>
/// Notification card hosted by a <see cref="ToastHost"/>: accent icon by <see cref="Kind"/>, a title, a message and
/// a close button (<see cref="CloseCommand"/>), which raises <see cref="CloseRequestedEvent"/>.
/// </summary>
public class Toast : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Toast), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(Toast), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ToastKind), typeof(Toast), new FrameworkPropertyMetadata(ToastKind.Info));

    /// <summary>Bubbles when the user dismisses the toast.</summary>
    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CloseRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(Toast));

    /// <summary>Command bound by the template's close button.</summary>
    public static readonly RoutedCommand CloseCommand = new(nameof(CloseCommand), typeof(Toast));

    static Toast()
    {
        CommandManager.RegisterClassCommandBinding(typeof(Toast), new CommandBinding(CloseCommand,
            (sender, _) => ((Toast)sender).RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, sender))));
    }

    /// <summary>Raised when the user dismisses the toast.</summary>
    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    /// <summary>Headline (localized by the caller).</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Detail line (localized by the caller).</summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Tone: accent color and icon.</summary>
    public ToastKind Kind
    {
        get => (ToastKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
