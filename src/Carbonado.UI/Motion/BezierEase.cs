using System.Windows;
using System.Windows.Media.Animation;

namespace Carbonado.UI.Motion;

/// <summary>
/// WPF easing function for a CSS-style cubic-bezier curve. The curve is used as-is: keep the default
/// <see cref="EasingFunctionBase.EasingMode"/> of <see cref="EasingMode.EaseIn"/> (other modes mirror it).
/// </summary>
public sealed class BezierEase : EasingFunctionBase
{
    public static readonly DependencyProperty X1Property = DependencyProperty.Register(
        nameof(X1), typeof(double), typeof(BezierEase), new PropertyMetadata(0.25, OnCurveChanged));

    public static readonly DependencyProperty Y1Property = DependencyProperty.Register(
        nameof(Y1), typeof(double), typeof(BezierEase), new PropertyMetadata(0.1, OnCurveChanged));

    public static readonly DependencyProperty X2Property = DependencyProperty.Register(
        nameof(X2), typeof(double), typeof(BezierEase), new PropertyMetadata(0.25, OnCurveChanged));

    public static readonly DependencyProperty Y2Property = DependencyProperty.Register(
        nameof(Y2), typeof(double), typeof(BezierEase), new PropertyMetadata(1.0, OnCurveChanged));

    private CubicBezier? _curve;

    public BezierEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    public double X1
    {
        get => (double)GetValue(X1Property);
        set => SetValue(X1Property, value);
    }

    public double Y1
    {
        get => (double)GetValue(Y1Property);
        set => SetValue(Y1Property, value);
    }

    public double X2
    {
        get => (double)GetValue(X2Property);
        set => SetValue(X2Property, value);
    }

    public double Y2
    {
        get => (double)GetValue(Y2Property);
        set => SetValue(Y2Property, value);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        _curve ??= new CubicBezier(X1, Y1, X2, Y2);
        return _curve.Value.Evaluate(normalizedTime);
    }

    protected override Freezable CreateInstanceCore() => new BezierEase();

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BezierEase)d)._curve = null;
}
