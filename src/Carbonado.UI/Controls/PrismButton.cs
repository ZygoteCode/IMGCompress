using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;
using Carbonado.UI.Theme;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// The signature call to action: Prisma gradient, iris glow, a sheen that sweeps across on hover (600 ms) and a
/// prismatic ripple that blooms from the press point. <see cref="IsBusy"/> swaps the icon for a spinning arc and
/// swallows clicks without looking disabled. Reduced motion: no sweep, the ripple becomes a short fade.
/// </summary>
[TemplatePart(Name = RippleHostPart, Type = typeof(Canvas))]
[TemplatePart(Name = RipplePart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = SpinnerPart, Type = typeof(FrameworkElement))]
public class PrismButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(PrismButton), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(PrismButton), new FrameworkPropertyMetadata(false, OnIsBusyChanged));

    private const string RippleHostPart = "PART_RippleHost";
    private const string RipplePart = "PART_Ripple";
    private const string SpinnerPart = "PART_Spinner";

    private Canvas? _rippleHost;
    private FrameworkElement? _ripple;
    private readonly ScaleTransform _rippleScale = new();
    private readonly RotateTransform _spin = new();
    private Point? _pressPoint;

    public PrismButton()
    {
        Loaded += (_, _) =>
        {
            MotionSettings.Changed -= OnMotionChanged;
            MotionSettings.Changed += OnMotionChanged;
            UpdateSpinner();
        };
        Unloaded += (_, _) =>
        {
            MotionSettings.Changed -= OnMotionChanged;
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        };
    }

    /// <summary>Leading icon (20×20 grid geometry).</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Shows a spinner instead of the icon and ignores clicks while true.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Where the last ripple started, in button coordinates (tests and diagnostics).</summary>
    internal Point? LastRippleOrigin { get; private set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_rippleHost is not null)
        {
            _rippleHost.SizeChanged -= OnRippleHostSizeChanged;
        }

        _rippleHost = GetTemplateChild(RippleHostPart) as Canvas;
        _ripple = GetTemplateChild(RipplePart) as FrameworkElement;
        if (_rippleHost is not null)
        {
            _rippleHost.SizeChanged += OnRippleHostSizeChanged;
        }

        if (_ripple is not null)
        {
            _ripple.RenderTransform = _rippleScale;
            _ripple.Opacity = 0;
        }

        if (GetTemplateChild(SpinnerPart) is FrameworkElement spinner)
        {
            spinner.RenderTransformOrigin = new Point(0.5, 0.5);
            spinner.RenderTransform = _spin;
        }

        UpdateSpinner();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        if (e.NewValue is true)
        {
            // Mouse presses ripple from the pointer; keyboard presses (Space/Enter) from the center.
            var origin = Mouse.LeftButton == MouseButtonState.Pressed && _pressPoint is { } p ? p : new Point(ActualWidth / 2, ActualHeight / 2);
            StartRipple(origin);
        }

        _pressPoint = null;
    }

    protected override void OnClick()
    {
        if (!IsBusy)
        {
            base.OnClick();
        }
    }

    /// <summary>Blooms the prismatic ripple from <paramref name="origin"/> (button coordinates).</summary>
    internal void StartRipple(Point origin)
    {
        LastRippleOrigin = origin;
        if (_rippleHost is null || _ripple is null || ActualWidth <= 0)
        {
            return;
        }

        // The disc must reach the farthest corner from the press point.
        double reach = new[]
        {
            (origin - new Point(0, 0)).Length,
            (origin - new Point(ActualWidth, 0)).Length,
            (origin - new Point(0, ActualHeight)).Length,
            (origin - new Point(ActualWidth, ActualHeight)).Length,
        }.Max();
        var local = TranslatePoint(origin, _rippleHost);
        _ripple.Width = _ripple.Height = reach * 2;
        Canvas.SetLeft(_ripple, local.X - reach);
        Canvas.SetTop(_ripple, local.Y - reach);
        _rippleScale.CenterX = _rippleScale.CenterY = reach;

        bool reduced = MotionSettings.ReducedMotion;
        var fade = new DoubleAnimation(reduced ? 0.35 : 0.7, 0, MotionSettings.Scale(TimeSpan.FromMilliseconds(reduced ? 120 : 650)))
        {
            EasingFunction = Eases.Glide,
        };
        fade.Freeze();
        _ripple.BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation(reduced ? 1 : 0.05, 1, MotionSettings.Scale(TimeSpan.FromMilliseconds(520)))
        {
            EasingFunction = Eases.Crystallize,
        };
        grow.Freeze();
        _rippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _rippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void OnRippleHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = Chrome.GetCornerRadius(this).TopLeft;
        var clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        clip.Freeze();
        ((FrameworkElement)sender).Clip = clip;
    }

    private void UpdateSpinner()
    {
        if (IsBusy && !MotionSettings.ReducedMotion)
        {
            var turn = new DoubleAnimation(0, 360, MotionSettings.Scale(TimeSpan.FromMilliseconds(900))) { RepeatBehavior = RepeatBehavior.Forever };
            turn.Freeze();
            _spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateSpinner);

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PrismButton)d).UpdateSpinner();
}
