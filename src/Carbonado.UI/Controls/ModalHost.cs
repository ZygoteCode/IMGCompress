using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Hosts the page (<see cref="ContentControl.Content"/>) and, while <see cref="IsOpen"/>, a modal
/// <see cref="Dialog"/> on a raised card over an obsidian scrim. The card rises 12 px and settles from 98.5 %
/// (420 ms Crystallize) and leaves with a 120 ms fade and 4 px drift; keyboard focus moves into the dialog,
/// stays there (Tab cycles) and returns afterwards; Esc closes when <see cref="CloseOnEscape"/>.
/// </summary>
[TemplatePart(Name = ScrimPart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = DialogPart, Type = typeof(FrameworkElement))]
public class ModalHost : ContentControl
{
    public static readonly DependencyProperty DialogProperty = DependencyProperty.Register(
        nameof(Dialog), typeof(object), typeof(ModalHost), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(ModalHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static readonly DependencyProperty CloseOnEscapeProperty = DependencyProperty.Register(
        nameof(CloseOnEscape), typeof(bool), typeof(ModalHost), new FrameworkPropertyMetadata(true));

    private const string ScrimPart = "PART_Scrim";
    private const string DialogPart = "PART_Dialog";

    private FrameworkElement? _scrim;
    private FrameworkElement? _dialog;
    private IInputElement? _restoreFocus;
    private int _generation;

    /// <summary>Dialog content (an element or a view-model rendered through a DataTemplate).</summary>
    public object? Dialog
    {
        get => GetValue(DialogProperty);
        set => SetValue(DialogProperty, value);
    }

    /// <summary>Shows or hides the dialog (two-way by default).</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Whether Esc closes the dialog (default true).</summary>
    public bool CloseOnEscape
    {
        get => (bool)GetValue(CloseOnEscapeProperty);
        set => SetValue(CloseOnEscapeProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scrim = GetTemplateChild(ScrimPart) as FrameworkElement;
        _dialog = GetTemplateChild(DialogPart) as FrameworkElement;
        Show(IsOpen, animate: false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc inside an open drop-down or menu closes that first, not the dialog.
        if (IsOpen && CloseOnEscape && e.Key == Key.Escape && !InOpenPopup(e.OriginalSource as DependencyObject))
        {
            IsOpen = false;
            e.Handled = true;
        }
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        // Keep focus inside the dialog while it is open.
        if (IsOpen && _dialog is not null && e.NewFocus is DependencyObject target && !IsInsideDialog(target))
        {
            e.Handled = true;
            _dialog.Dispatcher.BeginInvoke(() => _dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        }
    }

    /// <summary>True for the dialog and anything under it, including its popups (combo drop-downs, menus).</summary>
    private bool IsInsideDialog(DependencyObject? node)
    {
        for (; node is not null; node = UpOne(node))
        {
            if (ReferenceEquals(node, _dialog))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="node"/> sits in, or owns, an open popup (drop-down, menu, context menu).</summary>
    private static bool InOpenPopup(DependencyObject? node)
    {
        for (; node is not null; node = UpOne(node))
        {
            if (node is Popup { IsOpen: true } or ComboBox { IsDropDownOpen: true } or MenuItem { IsSubmenuOpen: true } or ContextMenu { IsOpen: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Next element up: a popup's child continues at its <see cref="Popup"/> (whose visual root is detached),
    /// otherwise the visual parent, falling back to the logical one.
    /// </summary>
    private static DependencyObject? UpOne(DependencyObject node)
    {
        var logical = LogicalTreeHelper.GetParent(node);
        if (logical is Popup)
        {
            return logical;
        }

        return (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? logical;
    }

    private void Show(bool open, bool animate)
    {
        if (_scrim is null || _dialog is null)
        {
            return;
        }

        int generation = ++_generation;
        bool reduced = MotionSettings.ReducedMotion;
        animate &= IsLoaded;
        if (open)
        {
            _restoreFocus = Keyboard.FocusedElement;
            _scrim.Visibility = _dialog.Visibility = Visibility.Visible;
            Fade(_scrim, 0, 1, TimeSpan.FromMilliseconds(160), animate);
            Fade(_dialog, 0, 1, TimeSpan.FromMilliseconds(260), animate);
            if (animate && !reduced)
            {
                var (scale, rise) = Transforms(_dialog);
                rise.BeginAnimation(TranslateTransform.YProperty, Tween(12, 0, TimeSpan.FromMilliseconds(420), Eases.Crystallize));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.985, 1, TimeSpan.FromMilliseconds(420), Eases.Crystallize));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.985, 1, TimeSpan.FromMilliseconds(420), Eases.Crystallize));
            }

            _dialog.Dispatcher.BeginInvoke(() => _dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        void Hide()
        {
            if (generation != _generation)
            {
                return;
            }

            _scrim.Visibility = _dialog.Visibility = Visibility.Collapsed;
            if (_restoreFocus is UIElement { IsVisible: true } previous)
            {
                previous.Focus();
            }

            _restoreFocus = null;
        }

        Fade(_scrim, 1, 0, TimeSpan.FromMilliseconds(160), animate);
        var fade = Fade(_dialog, 1, 0, TimeSpan.FromMilliseconds(120), animate);
        if (animate && !reduced)
        {
            Transforms(_dialog).Rise.BeginAnimation(TranslateTransform.YProperty, Tween(0, 4, TimeSpan.FromMilliseconds(120), Eases.Glide));
        }

        if (fade is null)
        {
            Hide();
        }
        else
        {
            fade.Completed += (_, _) => Hide();
        }
    }

    /// <summary>Fades <paramref name="element"/>; returns the clock so callers can wait for it (null when instant).</summary>
    private static AnimationClock? Fade(UIElement element, double from, double to, TimeSpan duration, bool animate)
    {
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = to;
            return null;
        }

        var clock = Tween(from, to, duration, Eases.Glide).CreateClock();
        element.ApplyAnimationClock(OpacityProperty, clock);
        return clock;
    }

    private static (ScaleTransform Scale, TranslateTransform Rise) Transforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform rise] })
        {
            return (scale, rise);
        }

        var created = (new ScaleTransform(), new TranslateTransform());
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { created.Item1, created.Item2 } };
        return created;
    }

    private static DoubleAnimation Tween(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(from, to, MotionSettings.Scale(duration)) { EasingFunction = ease };
        animation.Freeze();
        return animation;
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ModalHost)d).Show((bool)e.NewValue, animate: true);
}
