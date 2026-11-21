using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>
/// Draws a Carbonado icon: a geometry on the 20×20 icon grid (<c>Icon.*</c> resources), stroked with round caps
/// or filled, scaled uniformly to the element size (20×20 by default). <see cref="Foreground"/> inherits like text,
/// so icons inside buttons follow the button's foreground.
/// </summary>
public sealed class IconPresenter : FrameworkElement
{
    /// <summary>Size of the icon design grid.</summary>
    public const double GridSize = 20;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(IconPresenter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconPresenter),
        new FrameworkPropertyMetadata(
            TextElement.ForegroundProperty.DefaultMetadata.DefaultValue,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
            OnPenInputChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconPresenter),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender, OnPenInputChanged),
        value => value is double d && d >= 0 && double.IsFinite(d));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(IconPresenter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;

    static IconPresenter()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(IconPresenter), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Icon geometry in 20×20 grid units.</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Stroke (or fill) brush; inherited from the surrounding text foreground.</summary>
    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke width in grid units (scales with the icon).</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Fill the geometry instead of stroking it.</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(GridSize, availableSize.Width), Math.Min(GridSize, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var icon = Icon;
        var brush = Foreground;
        double side = Math.Min(ActualWidth, ActualHeight);
        if (icon is null || brush is null || side <= 0)
        {
            return;
        }

        double scale = side / GridSize;
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - side) / 2, (ActualHeight - side) / 2));
        if (Filled)
        {
            drawingContext.DrawGeometry(brush, null, icon);
        }
        else
        {
            drawingContext.DrawGeometry(null, _pen ??= CreatePen(brush, StrokeThickness), icon);
        }

        drawingContext.Pop();
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static void OnPenInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((IconPresenter)d)._pen = null;
}
