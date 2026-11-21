using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Carbonado.UI.Motion;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Compact column chart (savings timeline): rounded Prisma columns over hairline gridlines with optional labels.
/// Columns rise in a staggered wave when shown or when the data changes (instant under reduced motion).
/// Observes <see cref="INotifyCollectionChanged"/> collections while loaded (no leak through long-lived data).
/// </summary>
public sealed class BarChart : Control
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IList<double>), typeof(BarChart), new FrameworkPropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IList<string>), typeof(BarChart), new FrameworkPropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty BarBrushProperty = RegisterBrush(nameof(BarBrush));
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));

    private const double LabelBand = 18;
    private static readonly TimeSpan RiseTime = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(35);

    private readonly FrameTween _tween;
    private readonly CollectionObserver _observer;
    private double _progress = 1;

    public BarChart()
    {
        _tween = new FrameTween(p =>
        {
            _progress = p;
            InvalidateVisual();
        });
        _observer = new CollectionObserver(this, () => [Values, Labels], Replay);
        Loaded += (_, _) => Replay();
        Unloaded += (_, _) => _tween.Complete();
    }

    /// <summary>Column heights (non-finite and negative values draw nothing); scaled to the largest value.</summary>
    public IList<double>? Values
    {
        get => (IList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Optional label per column (thinned out automatically when they would overlap).</summary>
    public IList<string>? Labels
    {
        get => (IList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>Column fill (mapped over each whole column).</summary>
    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Hairline gridlines and baseline.</summary>
    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>Collections currently observed (0 while unloaded).</summary>
    internal int ObservedCollections => _observer.Count;

    protected override Size MeasureOverride(Size constraint) =>
        new(Math.Min(320, constraint.Width), Math.Min(140, constraint.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var values = Values;
        int count = values?.Count ?? 0;
        bool labelled = Labels is { Count: > 0 };
        var plot = new Rect(2, 6, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 6 - (labelled ? LabelBand : 0)));
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        DrawGrid(drawingContext, plot);
        if (values is null || count == 0 || BarBrush is not { } bar)
        {
            return;
        }

        double max = Math.Max(values.Where(double.IsFinite).DefaultIfEmpty(0).Max(), 1e-9);
        double slot = plot.Width / count;
        double width = Math.Clamp(slot * 0.58, 2, 28);
        double total = RiseTime.TotalMilliseconds + (StaggerStep.TotalMilliseconds * (count - 1));
        for (int i = 0; i < count; i++)
        {
            double v = double.IsFinite(values[i]) ? Math.Max(0, values[i]) : 0;
            double start = StaggerStep.TotalMilliseconds * i / total;
            double local = Math.Clamp((_progress - start) * total / RiseTime.TotalMilliseconds, 0, 1);
            double height = plot.Height * (v / max) * Eases.CrystallizeCurve.Evaluate(local);
            if (height < 0.5)
            {
                continue;
            }

            var column = new Rect(plot.X + (slot * i) + ((slot - width) / 2), plot.Bottom - height, width, height);
            drawingContext.DrawGeometry(bar, null, Column(column, Math.Min(3, Math.Min(width / 2, height))));
        }

        if (labelled)
        {
            DrawLabels(drawingContext, plot, slot);
        }
    }

    /// <summary>Column with rounded top corners and a square base.</summary>
    private static Geometry Column(Rect r, double radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var corner = new Size(radius, radius);
            ctx.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            ctx.LineTo(new Point(r.Left, r.Top + radius), false, false);
            ctx.ArcTo(new Point(r.Left + radius, r.Top), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Right - radius, r.Top), false, false);
            ctx.ArcTo(new Point(r.Right, r.Top + radius), corner, 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom), false, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        if (GridBrush is not { } grid)
        {
            return;
        }

        var pen = new Pen(grid, 1);
        pen.Freeze();
        for (int i = 0; i <= 3; i++)
        {
            double y = Math.Round(plot.Top + (plot.Height * i / 3)) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private void DrawLabels(DrawingContext dc, Rect plot, double slot)
    {
        var labels = Labels!;
        if (Foreground is not { } brush)
        {
            return;
        }

        var face = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var texts = labels.Select(l => new FormattedText(l ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection, face, 10, brush, null,
            TextFormattingMode.Display, dip)).ToArray();
        double widest = texts.Length == 0 ? 0 : texts.Max(t => t.WidthIncludingTrailingWhitespace) + 6;
        int every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, slot)));
        for (int i = 0; i < texts.Length && i < (Values?.Count ?? 0); i += every)
        {
            double x = plot.X + (slot * i) + ((slot - texts[i].WidthIncludingTrailingWhitespace) / 2);
            dc.DrawText(texts[i], new Point(x, plot.Bottom + 4));
        }
    }

    /// <summary>Plays the staggered rise again (when shown or when the data changes).</summary>
    private void Replay()
    {
        if (IsLoaded && IsVisible)
        {
            int count = Values?.Count ?? 0;
            var span = RiseTime + (StaggerStep * Math.Max(0, count - 1));
            _tween.Start(span, new CubicBezier(0, 0, 1, 1));
        }
        else
        {
            _tween.Complete();
        }
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (BarChart)d;
        chart._observer.Refresh();
        chart.Replay();
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
