using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Carbonado.UI.Motion;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Per-item status ring shaped like the brilliant cut's octagonal crown. Working: the Prisma stroke traces the
/// facets up to <see cref="Value"/> (or a facet arc spins when <see cref="IsIndeterminate"/>); Done / Kept /
/// Failed close the ring in emerald / topaz / ruby and pop a glyph in. Lightweight (drawn, no template, no
/// effects): safe for hundreds of queue rows; frames are produced only while something moves.
/// </summary>
public sealed class GemProgressRing : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GemProgressRing),
        new FrameworkPropertyMetadata(0.0, OnValueChanged, CoerceFraction));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(GemProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnSpinInputChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(GemState), typeof(GemProgressRing),
        new FrameworkPropertyMetadata(GemState.Idle, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(GemProgressRing),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
    public static readonly DependencyProperty ProgressBrushProperty = RegisterBrush(nameof(ProgressBrush));
    public static readonly DependencyProperty SuccessBrushProperty = RegisterBrush(nameof(SuccessBrush));
    public static readonly DependencyProperty KeptBrushProperty = RegisterBrush(nameof(KeptBrush));
    public static readonly DependencyProperty FailedBrushProperty = RegisterBrush(nameof(FailedBrush));

    private static readonly TimeSpan ValueTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan SpinTime = TimeSpan.FromSeconds(1.1);

    private readonly DrawingVisual _spinner = new();
    private readonly DrawingVisual _glyph = new();
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _pop = new();
    private readonly FrameTween _tween;
    private double _shown;
    private double _tweenFrom;
    private double _tweenTo;

    static GemProgressRing()
    {
        FocusableProperty.OverrideMetadata(typeof(GemProgressRing), new FrameworkPropertyMetadata(false));
        ForegroundProperty.OverrideMetadata(typeof(GemProgressRing), new FrameworkPropertyMetadata(OnLookChanged));
    }

    public GemProgressRing()
    {
        _tween = new FrameTween(p =>
        {
            _shown = _tweenFrom + ((_tweenTo - _tweenFrom) * p);
            InvalidateVisual();
        });
        _spinner.Transform = _spin;
        _glyph.Transform = _pop;
        AddVisualChild(_spinner);
        AddVisualChild(_glyph);
        Unloaded += (_, _) =>
        {
            MotionSettings.Changed -= OnMotionChanged;
            StopMotion();
        };
        Loaded += (_, _) =>
        {
            MotionSettings.Changed -= OnMotionChanged;
            MotionSettings.Changed += OnMotionChanged;
            UpdateSpin();
        };
        IsVisibleChanged += (_, _) => UpdateSpin();
    }

    /// <summary>Progress 0..1 (clamped); animates smoothly to new values.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Spinning facet instead of measured progress (while <see cref="State"/> is Working).</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Lifecycle state: picks the ring color and the center glyph.</summary>
    public GemState State
    {
        get => (GemState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Stroke width of the ring in DIPs.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>Dim octagon under the progress.</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Progress stroke and indeterminate facet.</summary>
    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    /// <summary>Ring and check when Done.</summary>
    public Brush? SuccessBrush
    {
        get => (Brush?)GetValue(SuccessBrushProperty);
        set => SetValue(SuccessBrushProperty, value);
    }

    /// <summary>Ring and equals mark when Kept.</summary>
    public Brush? KeptBrush
    {
        get => (Brush?)GetValue(KeptBrushProperty);
        set => SetValue(KeptBrushProperty, value);
    }

    /// <summary>Ring and cross when Failed.</summary>
    public Brush? FailedBrush
    {
        get => (Brush?)GetValue(FailedBrushProperty);
        set => SetValue(FailedBrushProperty, value);
    }

    /// <summary>Progress currently drawn (follows <see cref="Value"/> with a short ease).</summary>
    internal double ShownValue => _shown;

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _spinner,
        1 => _glyph,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size constraint) => new(Math.Min(28, constraint.Width), Math.Min(28, constraint.Height));

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var center = Center(arrangeBounds);
        _spin.CenterX = _pop.CenterX = center.X;
        _spin.CenterY = _pop.CenterY = center.Y;
        DrawSpinner(arrangeBounds);
        DrawGlyph(arrangeBounds);
        return arrangeBounds;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        double radius = Radius(size);
        if (radius <= 0)
        {
            return;
        }

        var center = Center(size);
        if (TrackBrush is { } track)
        {
            drawingContext.DrawGeometry(null, Stroke(track), Arcs.Octagon(center, radius));
        }

        var (ring, fraction) = State switch
        {
            GemState.Working when !IsIndeterminate => (ProgressBrush, _shown),
            GemState.Done => (SuccessBrush, 1.0),
            GemState.Kept => (KeptBrush, 1.0),
            GemState.Failed => (FailedBrush, 1.0),
            _ => (null, 0.0),
        };
        if (ring is not null && fraction > 0.001)
        {
            drawingContext.DrawGeometry(null, Stroke(ring), Arcs.Octagon(center, radius, fraction));
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GemProgressRingAutomationPeer(this);

    private void DrawSpinner(Size size)
    {
        using var dc = _spinner.RenderOpen();
        double radius = Radius(size);
        if (State == GemState.Working && IsIndeterminate && ProgressBrush is { } brush && radius > 0)
        {
            dc.DrawGeometry(null, Stroke(brush), Arcs.Octagon(Center(size), radius, 0.3));
        }
    }

    private void DrawGlyph(Size size)
    {
        using var dc = _glyph.RenderOpen();
        double r = Radius(size) * 0.62;
        if (r <= 0)
        {
            return;
        }

        var c = Center(size);
        Point P(double x, double y) => new(c.X + (x * r), c.Y + (y * r));
        switch (State)
        {
            case GemState.Done when SuccessBrush is { } success:
                DrawPolyline(dc, Stroke(success, 1.1), P(-0.5, 0.02), P(-0.15, 0.38), P(0.55, -0.36));
                break;
            case GemState.Kept when KeptBrush is { } kept:
                DrawPolyline(dc, Stroke(kept, 1.1), P(-0.45, -0.18), P(0.45, -0.18));
                DrawPolyline(dc, Stroke(kept, 1.1), P(-0.45, 0.2), P(0.45, 0.2));
                break;
            case GemState.Failed when FailedBrush is { } failed:
                DrawPolyline(dc, Stroke(failed, 1.1), P(-0.38, -0.38), P(0.38, 0.38));
                DrawPolyline(dc, Stroke(failed, 1.1), P(0.38, -0.38), P(-0.38, 0.38));
                break;
            case GemState.Idle or GemState.Working when Foreground is { } gem:
                // A small brilliant: crown, girdle and pavilion point.
                var pen = Stroke(gem, 0.7);
                DrawPolyline(dc, pen, P(-0.5, -0.12), P(-0.25, -0.42), P(0.25, -0.42), P(0.5, -0.12), P(0, 0.5), P(-0.5, -0.12));
                DrawPolyline(dc, pen, P(-0.5, -0.12), P(0.5, -0.12));
                break;
        }
    }

    private static void DrawPolyline(DrawingContext dc, Pen pen, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++)
            {
                ctx.LineTo(points[i], true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private Pen Stroke(Brush brush, double scale = 1)
    {
        var pen = new Pen(brush, Thickness * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private double Radius(Size size) => (Math.Min(size.Width, size.Height) / 2) - (Thickness / 2) - 0.5;

    private static Point Center(Size size) => new(size.Width / 2, size.Height / 2);

    private void UpdateSpin()
    {
        bool spin = State == GemState.Working && IsIndeterminate && IsLoaded && IsVisible && !MotionSettings.ReducedMotion;
        if (spin && !_spin.HasAnimatedProperties)
        {
            var turn = new DoubleAnimation(0, 360, MotionSettings.Scale(SpinTime)) { RepeatBehavior = RepeatBehavior.Forever };
            turn.Freeze();
            _spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else if (!spin)
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateSpin);

    private void StopMotion()
    {
        _tween.Complete();
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Pop()
    {
        if (!IsLoaded || MotionSettings.ReducedMotion)
        {
            return;
        }

        var grow = new DoubleAnimation(0.55, 1, MotionSettings.Scale(TimeSpan.FromMilliseconds(320))) { EasingFunction = Eases.Settle, FillBehavior = FillBehavior.Stop };
        grow.Freeze();
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (GemProgressRing)d;
        ring._tweenFrom = ring._shown;
        ring._tweenTo = (double)e.NewValue;
        if (ring.IsLoaded && ring.IsVisible)
        {
            ring._tween.Start(ValueTime, Eases.CrystallizeCurve);
        }
        else
        {
            ring._tween.Complete();
        }
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (GemProgressRing)d;
        ring.InvalidateArrange();
        ring.UpdateSpin();
        if ((GemState)e.NewValue is GemState.Done or GemState.Kept or GemState.Failed)
        {
            ring.Pop();
        }
    }

    private static void OnSpinInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (GemProgressRing)d;
        ring.InvalidateArrange();
        ring.UpdateSpin();
    }

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (GemProgressRing)d;
        ring.InvalidateArrange();
        ring.InvalidateVisual();
    }

    private static object CoerceFraction(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.0;

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(GemProgressRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    /// <summary>Exposes the ring to UI Automation as a progress bar.</summary>
    private sealed class GemProgressRingAutomationPeer(GemProgressRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

        protected override string GetClassNameCore() => nameof(GemProgressRing);
    }
}
