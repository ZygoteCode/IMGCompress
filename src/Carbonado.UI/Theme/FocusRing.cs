using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Theme;

/// <summary>
/// Keyboard focus ring drawn around (or inside) its bounds: a <see cref="RingBrush"/> stroke with an optional soft
/// <see cref="HaloBrush"/> outside it. The radius is clamped to half the ring height, so a large
/// <see cref="CornerRadius"/> always yields a true pill (a WPF Border would turn into an ellipse).
/// Used by the <c>Focus.Prisma*</c> focus visual styles.
/// </summary>
public sealed class FocusRing : FrameworkElement
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(FocusRing),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutsetProperty = DependencyProperty.Register(
        nameof(Outset), typeof(double), typeof(FocusRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(FocusRing),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(FocusRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HaloBrushProperty = DependencyProperty.Register(
        nameof(HaloBrush), typeof(Brush), typeof(FocusRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner radius of the focused control's edge; the ring adds <see cref="Outset"/>.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Distance between the control edge and the ring's outer edge (negative = inside).</summary>
    public double Outset
    {
        get => (double)GetValue(OutsetProperty);
        set => SetValue(OutsetProperty, value);
    }

    /// <summary>Stroke width of the ring.</summary>
    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    /// <summary>Ring stroke (the theme uses Prisma).</summary>
    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>Optional 2 px glow just outside the ring.</summary>
    public Brush? HaloBrush
    {
        get => (Brush?)GetValue(HaloBrushProperty);
        set => SetValue(HaloBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double outset = Outset;
        double thickness = RingThickness;
        var bounds = new Rect(-outset, -outset, ActualWidth + (2 * outset), ActualHeight + (2 * outset));
        if (bounds.Width <= thickness || bounds.Height <= thickness)
        {
            return;
        }

        if (HaloBrush is { } halo)
        {
            Stroke(drawingContext, halo, 2, Inflate(bounds, 2), CornerRadius + outset + 2);
        }

        if (RingBrush is { } ring)
        {
            Stroke(drawingContext, ring, thickness, bounds, CornerRadius + outset);
        }
    }

    private static void Stroke(DrawingContext dc, Brush brush, double thickness, Rect outer, double radius)
    {
        // Center the stroke inside the outer edge so the ring never grows past it.
        var rect = Inflate(outer, -thickness / 2);
        double r = Math.Clamp(radius - (thickness / 2), 0, Math.Min(rect.Width, rect.Height) / 2);
        dc.DrawRoundedRectangle(null, new Pen(brush, thickness), rect, r, r);
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }
}
