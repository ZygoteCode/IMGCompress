using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// 270° savings gauge: a Prisma arc sweeps to <see cref="Value"/> (0..1, clamped) over an obsidian track with
/// hairline decile ticks, tipped by a glowing diamond spark; <see cref="ValueText"/> sits in the center over a
/// <see cref="Caption"/>. Drawn (no template); the sweep animates 720 ms Crystallize (instant under reduced motion).
/// </summary>
public sealed class RadialGauge : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(0.0, OnValueChanged, CoerceFraction));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RadialGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(RadialGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
    public static readonly DependencyProperty ValueBrushProperty = RegisterBrush(nameof(ValueBrush));
    public static readonly DependencyProperty TickBrushProperty = RegisterBrush(nameof(TickBrush));
    public static readonly DependencyProperty SparkBrushProperty = RegisterBrush(nameof(SparkBrush));
    public static readonly DependencyProperty CaptionBrushProperty = RegisterBrush(nameof(CaptionBrush));

    /// <summary>Start angle (clockwise from 12 o'clock) and total sweep of the dial.</summary>
    internal const double StartAngle = -135;
    internal const double SweepAngle = 270;

    private readonly FrameTween _tween;
    private double _shown;
    private double _from;
    private double _to;

    public RadialGauge()
    {
        _tween = new FrameTween(p =>
        {
            _shown = _from + ((_to - _from) * p);
            InvalidateVisual();
        });
        Unloaded += (_, _) => _tween.Complete();
    }

    /// <summary>Fill fraction 0..1; out-of-range and non-finite values are clamped.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Label under the value (localized by the caller).</summary>
    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Text in the center, e.g. "−91 %" (formatted by the caller).</summary>
    public string? ValueText
    {
        get => (string?)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    /// <summary>Full 270° track under the value arc.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Value arc.</summary>
    public Brush? ValueBrush
    {
        get => (Brush?)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    /// <summary>Decile tick marks inside the arc.</summary>
    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /// <summary>Radial glow drawn at the tip of the value arc.</summary>
    public Brush? SparkBrush
    {
        get => (Brush?)GetValue(SparkBrushProperty);
        set => SetValue(SparkBrushProperty, value);
    }

    /// <summary>Caption text.</summary>
    public Brush? CaptionBrush
    {
        get => (Brush?)GetValue(CaptionBrushProperty);
        set => SetValue(CaptionBrushProperty, value);
    }

    /// <summary>Fraction currently drawn (follows <see cref="Value"/>).</summary>
    internal double ShownValue => _shown;

    protected override Size MeasureOverride(Size constraint) =>
        new(Math.Min(180, constraint.Width), Math.Min(180, constraint.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side < 24)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double thickness = Math.Max(5, side * 0.06);
        double radius = (side / 2) - (thickness * 1.2);
        DrawTicks(drawingContext, center, radius - (thickness * 1.25), thickness * 0.45);
        if (TrackBrush is { } track)
        {
            drawingContext.DrawGeometry(null, Cap(track, thickness), Arcs.Arc(center, radius, StartAngle, SweepAngle));
        }

        if (_shown > 0.002 && ValueBrush is { } value)
        {
            drawingContext.DrawGeometry(null, Cap(value, thickness), Arcs.Arc(center, radius, StartAngle, SweepAngle * _shown));
            if (SparkBrush is { } spark)
            {
                var tip = Arcs.On(center, radius, StartAngle + (SweepAngle * _shown));
                drawingContext.DrawEllipse(spark, null, tip, thickness * 1.7, thickness * 1.7);
            }
        }

        DrawLabels(drawingContext, center, side);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RadialGaugeAutomationPeer(this);

    private void DrawTicks(DrawingContext dc, Point center, double radius, double length)
    {
        if (TickBrush is not { } brush)
        {
            return;
        }

        var pen = new Pen(brush, 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        for (int i = 0; i <= 10; i++)
        {
            double angle = StartAngle + (SweepAngle * i / 10);
            double inner = i % 5 == 0 ? radius - (length * 1.8) : radius - length;
            dc.DrawLine(pen, Arcs.On(center, inner, angle), Arcs.On(center, radius, angle));
        }
    }

    private void DrawLabels(DrawingContext dc, Point center, double side)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText? value = null;
        if (!string.IsNullOrEmpty(ValueText) && Foreground is { } fg)
        {
            value = new FormattedText(ValueText, CultureInfo.CurrentUICulture, FlowDirection, Face(FontWeights.SemiBold),
                side * 0.2, fg, null, TextFormattingMode.Ideal, dip);
        }

        FormattedText? caption = null;
        if (!string.IsNullOrEmpty(Caption) && CaptionBrush is { } cb)
        {
            caption = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection, Face(FontWeights.Medium),
                Math.Max(10, side * 0.068), cb, null, TextFormattingMode.Ideal, dip);
        }

        double total = (value?.Height ?? 0) + (caption?.Height ?? 0);
        double y = center.Y - (total / 2) + (side * 0.03);
        if (value is not null)
        {
            dc.DrawText(value, new Point(center.X - (value.WidthIncludingTrailingWhitespace / 2), y));
            y += value.Height;
        }

        if (caption is not null)
        {
            dc.DrawText(caption, new Point(center.X - (caption.WidthIncludingTrailingWhitespace / 2), y));
        }
    }

    private Typeface Face(FontWeight weight) => new(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);

    private static Pen Cap(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RadialGauge)d;
        gauge._from = gauge._shown;
        gauge._to = (double)e.NewValue;
        if (gauge.IsLoaded && gauge.IsVisible)
        {
            gauge._tween.Start(TimeSpan.FromMilliseconds(720), Eases.CrystallizeCurve);
        }
        else
        {
            gauge._tween.Complete();
        }
    }

    private static object CoerceFraction(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.0;

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(RadialGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private sealed class RadialGaugeAutomationPeer(RadialGauge owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

        protected override string GetClassNameCore() => nameof(RadialGauge);

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            var gauge = (RadialGauge)Owner;
            return string.IsNullOrEmpty(name) ? $"{gauge.ValueText} {gauge.Caption}".Trim() : name;
        }
    }
}
