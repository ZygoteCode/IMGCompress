using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Carbonado.UI.Transitions;

namespace Carbonado.UI.Controls;

/// <summary>
/// Format-distribution donut: slices proportional to their values, separated by thin gaps, revealed by a
/// clockwise sweep (Crystallize, instant under reduced motion). Observes <see cref="INotifyCollectionChanged"/>
/// slices while loaded (no leak through long-lived data).
/// </summary>
public sealed class DonutChart : Control
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IList<DonutSlice>), typeof(DonutChart), new FrameworkPropertyMetadata(null, OnSlicesChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Ring thickness as a fraction of the radius.</summary>
    public static readonly DependencyProperty ThicknessRatioProperty = DependencyProperty.Register(
        nameof(ThicknessRatio), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(0.22, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gap between slices in degrees.</summary>
    public const double GapDegrees = 2.4;

    private readonly FrameTween _tween;
    private readonly CollectionObserver _observer;
    private double _reveal = 1;

    public DonutChart()
    {
        _tween = new FrameTween(p =>
        {
            _reveal = p;
            InvalidateVisual();
        });
        _observer = new CollectionObserver(this, () => [Slices], Replay);
        Loaded += (_, _) => Replay();
        Unloaded += (_, _) => _tween.Complete();
    }

    /// <summary>Segments, drawn clockwise from 12 o'clock.</summary>
    public IList<DonutSlice>? Slices
    {
        get => (IList<DonutSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <summary>Ring shown under the slices (and while they sweep in).</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Ring thickness as a fraction of the radius (0.05…0.9).</summary>
    public double ThicknessRatio
    {
        get => (double)GetValue(ThicknessRatioProperty);
        set => SetValue(ThicknessRatioProperty, value);
    }

    /// <summary>Collections currently observed (0 while unloaded).</summary>
    internal int ObservedCollections => _observer.Count;

    /// <summary>
    /// Angular size of each slice in degrees: proportional to its value, 0 for non-positive or non-finite values.
    /// The sweeps sum to 360 whenever any value is positive (gaps are carved inside each slice when drawing).
    /// </summary>
    public static IReadOnlyList<double> ComputeSweeps(IEnumerable<DonutSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        var values = slices.Select(s => double.IsFinite(s.Value) && s.Value > 0 ? s.Value : 0).ToArray();
        double total = values.Sum();
        return total > 0 ? values.Select(v => v / total * 360).ToArray() : new double[values.Length];
    }

    protected override Size MeasureOverride(Size constraint) =>
        new(Math.Min(160, constraint.Width), Math.Min(160, constraint.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side < 8)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = (side / 2) - 1;
        double inner = outer * (1 - Math.Clamp(ThicknessRatio, 0.05, 0.9));
        if (TrackBrush is { } track)
        {
            var band = new Pen(track, outer - inner);
            band.Freeze();
            drawingContext.DrawEllipse(null, band, center, (outer + inner) / 2, (outer + inner) / 2);
        }

        if (Slices is not { Count: > 0 } slices)
        {
            return;
        }

        var sweeps = ComputeSweeps(slices);
        double limit = 360 * _reveal;
        double start = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            double sweep = sweeps[i];
            double gap = sweep > GapDegrees * 2 && sweeps.Count(s => s > 0) > 1 ? GapDegrees : 0;
            double from = start + (gap / 2);
            double to = Math.Min(start + sweep - (gap / 2), limit);
            if (to > from && slices[i].Brush is { } brush)
            {
                drawingContext.DrawGeometry(brush, null, Arcs.Sector(center, outer, inner, from, to - from));
            }

            start += sweep;
        }
    }

    private void Replay()
    {
        if (IsLoaded && IsVisible)
        {
            _tween.Start(TimeSpan.FromMilliseconds(720), Eases.CrystallizeCurve);
        }
        else
        {
            _tween.Complete();
        }
    }

    private static void OnSlicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (DonutChart)d;
        chart._observer.Refresh();
        chart.Replay();
    }
}
