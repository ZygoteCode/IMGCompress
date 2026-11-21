using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Carbonado.UI.Controls;

/// <summary>
/// Compare view for an original and its compressed output: split divider, side-by-side, flicker (A/B) or
/// difference heat-map. Zoom and pan are shared by every pane (synchronized); the mouse wheel zooms around the
/// cursor, dragging pans (or moves the divider), <c>+</c>/<c>-</c>/<c>0</c>/<c>1</c> zoom from the keyboard.
/// <see cref="Zoom"/> 1 means one image pixel per device pixel; <see cref="Pan"/> is the image's top-left corner
/// in pane coordinates. Magnified views switch to nearest-neighbour so pixels stay crisp.
/// </summary>
public sealed partial class BeforeAfterViewer : Control
{
    public static readonly DependencyProperty BeforeProperty = RegisterImage(nameof(Before));
    public static readonly DependencyProperty AfterProperty = RegisterImage(nameof(After));
    public static readonly DependencyProperty HeatmapProperty = RegisterImage(nameof(Heatmap));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(CompareMode), typeof(BeforeAfterViewer),
        new FrameworkPropertyMetadata(CompareMode.Split, FrameworkPropertyMetadataOptions.AffectsRender, OnModeChanged));

    public static readonly DependencyProperty SplitProperty = DependencyProperty.Register(
        nameof(Split), typeof(double), typeof(BeforeAfterViewer),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceSplit));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(BeforeAfterViewer),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender, OnZoomChanged, CoerceZoom));

    public static readonly DependencyProperty PanProperty = DependencyProperty.Register(
        nameof(Pan), typeof(Point), typeof(BeforeAfterViewer),
        new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BeforeLabelProperty = RegisterText(nameof(BeforeLabel));
    public static readonly DependencyProperty AfterLabelProperty = RegisterText(nameof(AfterLabel));
    public static readonly DependencyProperty CheckerBrushProperty = RegisterBrush(nameof(CheckerBrush));
    public static readonly DependencyProperty DividerBrushProperty = RegisterBrush(nameof(DividerBrush));
    public static readonly DependencyProperty KnobBrushProperty = RegisterBrush(nameof(KnobBrush));
    public static readonly DependencyProperty LabelBackgroundProperty = RegisterBrush(nameof(LabelBackground));

    /// <summary>Zoom range: 2 % … 3200 %.</summary>
    public const double MinZoom = 0.02;
    /// <summary>Upper zoom bound (3200 %).</summary>
    public const double MaxZoom = 32;

    private const double WheelStep = 1.2;
    private const double DividerGrip = 10;
    private static readonly TimeSpan FlickerInterval = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _flicker;
    private bool _fitPending = true;
    private Point? _dragStart;
    private Point _dragPan;
    private bool _draggingDivider;

    static BeforeAfterViewer()
    {
        FocusableProperty.OverrideMetadata(typeof(BeforeAfterViewer), new FrameworkPropertyMetadata(true));
        ClipToBoundsProperty.OverrideMetadata(typeof(BeforeAfterViewer), new FrameworkPropertyMetadata(true));
    }

    public BeforeAfterViewer()
    {
        ZoomToFitCommand = new RelayCommand(ZoomToFit, () => ImageSize is not null);
        ZoomToActualCommand = new RelayCommand(ZoomToActual, () => ImageSize is not null);
        _flicker = new DispatcherTimer(DispatcherPriority.Render) { Interval = FlickerInterval };
        _flicker.Tick += (_, _) =>
        {
            IsShowingBefore = !IsShowingBefore;
            InvalidateVisual();
        };
        Loaded += (_, _) => UpdateFlicker();
        Unloaded += (_, _) => _flicker.Stop();
        SizeChanged += (_, _) => FitIfPending();
    }

    /// <summary>The original image.</summary>
    public ImageSource? Before
    {
        get => (ImageSource?)GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    /// <summary>The compressed result (defines the image size when present).</summary>
    public ImageSource? After
    {
        get => (ImageSource?)GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    /// <summary>Per-pixel error map (e.g. SSIMULACRA2), same size as the images, shown in <see cref="CompareMode.Heatmap"/>.</summary>
    public ImageSource? Heatmap
    {
        get => (ImageSource?)GetValue(HeatmapProperty);
        set => SetValue(HeatmapProperty, value);
    }

    /// <summary>How the two images are presented.</summary>
    public CompareMode Mode
    {
        get => (CompareMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Divider position in <see cref="CompareMode.Split"/>, 0 (left) … 1 (right).</summary>
    public double Split
    {
        get => (double)GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    /// <summary>Magnification: 1 = one image pixel per device pixel (clamped to <see cref="MinZoom"/>…<see cref="MaxZoom"/>).</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Top-left corner of the image in pane coordinates (DIPs); the same for every pane.</summary>
    public Point Pan
    {
        get => (Point)GetValue(PanProperty);
        set => SetValue(PanProperty, value);
    }

    /// <summary>Chip text over the original (localized by the caller; null hides the chip).</summary>
    public string? BeforeLabel
    {
        get => (string?)GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    /// <summary>Chip text over the result (localized by the caller; null hides the chip).</summary>
    public string? AfterLabel
    {
        get => (string?)GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    /// <summary>Transparency backdrop drawn under the images.</summary>
    public Brush? CheckerBrush
    {
        get => (Brush?)GetValue(CheckerBrushProperty);
        set => SetValue(CheckerBrushProperty, value);
    }

    /// <summary>Split divider, knob outline and side-by-side gap.</summary>
    public Brush? DividerBrush
    {
        get => (Brush?)GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    /// <summary>Fill of the divider knob.</summary>
    public Brush? KnobBrush
    {
        get => (Brush?)GetValue(KnobBrushProperty);
        set => SetValue(KnobBrushProperty, value);
    }

    /// <summary>Fill of the Before/After chips.</summary>
    public Brush? LabelBackground
    {
        get => (Brush?)GetValue(LabelBackgroundProperty);
        set => SetValue(LabelBackgroundProperty, value);
    }

    /// <summary>Fits the whole image in the pane, centered.</summary>
    public ICommand ZoomToFitCommand { get; }

    /// <summary>100 %: one image pixel per device pixel, keeping the pane center fixed.</summary>
    public ICommand ZoomToActualCommand { get; }

    /// <summary>In <see cref="CompareMode.Flicker"/>, whether Before is currently shown.</summary>
    public bool IsShowingBefore { get; private set; }

    /// <summary>Image size in pixels (from After, else Before), or null without images.</summary>
    internal Size? ImageSize => PixelSize(After) ?? PixelSize(Before);

    /// <summary>DIPs per image pixel at the current zoom.</summary>
    private double Scale => Zoom / DpiScale;

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Maps a point in control coordinates to image pixel coordinates (through the pane it falls in).</summary>
    public Point ViewToImage(Point view)
    {
        var pane = PaneAt(view);
        return new Point((view.X - pane.X - Pan.X) / Scale, (view.Y - pane.Y - Pan.Y) / Scale);
    }

    /// <summary>Maps image pixel coordinates to control coordinates in the first (left) pane.</summary>
    public Point ImageToView(Point image) =>
        new(Pan.X + (image.X * Scale), Pan.Y + (image.Y * Scale));

    /// <summary>Sets <see cref="Zoom"/> to <paramref name="zoom"/> keeping the image point under <paramref name="view"/> fixed.</summary>
    public void ZoomAt(Point view, double zoom)
    {
        var anchor = ViewToImage(view);
        var pane = PaneAt(view);
        Zoom = zoom;
        Pan = new Point(view.X - pane.X - (anchor.X * Scale), view.Y - pane.Y - (anchor.Y * Scale));
        _fitPending = false;
    }

    /// <summary>Fits the whole image in the pane, centered (deferred until the control has a size).</summary>
    public void ZoomToFit()
    {
        if (ImageSize is not { } image)
        {
            return;
        }

        var pane = PaneSize;
        if (pane.Width <= 0 || pane.Height <= 0)
        {
            _fitPending = true;
            return;
        }

        double scale = Math.Min(pane.Width / image.Width, pane.Height / image.Height);
        Zoom = scale * DpiScale;
        Pan = new Point((pane.Width - (image.Width * Scale)) / 2, (pane.Height - (image.Height * Scale)) / 2);
        _fitPending = false;
    }

    /// <summary>Zooms to 100 % around the pane center.</summary>
    public void ZoomToActual()
    {
        var pane = PaneSize;
        ZoomAt(new Point(pane.Width / 2, pane.Height / 2), 1);
    }

    protected override Size MeasureOverride(Size constraint) =>
        new(double.IsInfinity(constraint.Width) ? 480 : constraint.Width, double.IsInfinity(constraint.Height) ? 320 : constraint.Height);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ImageSize is null)
        {
            return;
        }

        ZoomAt(e.GetPosition(this), Zoom * Math.Pow(WheelStep, e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        _draggingDivider = Mode == CompareMode.Split && Math.Abs(point.X - (Split * ActualWidth)) <= DividerGrip;
        _dragStart = point;
        _dragPan = Pan;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        Cursor = _draggingDivider || (Mode == CompareMode.Split && Math.Abs(point.X - (Split * ActualWidth)) <= DividerGrip)
            ? Cursors.SizeWE
            : _dragStart is null ? Cursors.Hand : Cursors.ScrollAll;
        if (_dragStart is not { } start || !IsMouseCaptured)
        {
            return;
        }

        if (_draggingDivider)
        {
            Split = point.X / Math.Max(1, ActualWidth);
        }
        else
        {
            Pan = _dragPan + (point - start);
            _fitPending = false;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragStart = null;
        _draggingDivider = false;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var center = new Point(PaneSize.Width / 2, PaneSize.Height / 2);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomAt(center, Zoom * WheelStep);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomAt(center, Zoom / WheelStep);
                break;
            case Key.D0 or Key.NumPad0:
                ZoomToFit();
                break;
            case Key.D1 or Key.NumPad1:
                ZoomToActual();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var step = e.Key switch { Key.Left => new Vector(40, 0), Key.Right => new Vector(-40, 0), Key.Up => new Vector(0, 40), _ => new Vector(0, -40) };
                Pan += step;
                _fitPending = false;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Pane size: the whole control, or half of it side by side.</summary>
    private Size PaneSize => Mode == CompareMode.SideBySide
        ? new Size(Math.Max(0, (ActualWidth - 1) / 2), ActualHeight)
        : new Size(ActualWidth, ActualHeight);

    /// <summary>Origin of the pane containing <paramref name="view"/>.</summary>
    private Point PaneAt(Point view) =>
        Mode == CompareMode.SideBySide && view.X >= ActualWidth / 2 ? new Point(PaneSize.Width + 1, 0) : default;

    private void FitIfPending()
    {
        if (_fitPending)
        {
            ZoomToFit();
        }
    }

    private void UpdateFlicker()
    {
        IsShowingBefore = false;
        if (Mode == CompareMode.Flicker && IsLoaded)
        {
            _flicker.Start();
        }
        else
        {
            _flicker.Stop();
        }
    }

    private static Size? PixelSize(ImageSource? source) => source switch
    {
        BitmapSource bitmap => new Size(bitmap.PixelWidth, bitmap.PixelHeight),
        { Width: > 0, Height: > 0 } other => new Size(other.Width, other.Height),
        _ => null,
    };

    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (BeforeAfterViewer)d;
        if (PixelSize(e.OldValue as ImageSource) != PixelSize(e.NewValue as ImageSource))
        {
            viewer._fitPending = true;
            viewer.FitIfPending();
        }

        viewer.InvalidateVisual();
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (BeforeAfterViewer)d;
        viewer.UpdateFlicker();
        if (viewer._fitPending || (CompareMode)e.NewValue == CompareMode.SideBySide || (CompareMode)e.OldValue == CompareMode.SideBySide)
        {
            viewer.ZoomToFit();
        }
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (BeforeAfterViewer)d;
        RenderOptions.SetBitmapScalingMode(viewer, viewer.Scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private static object CoerceSplit(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5;

    private static object CoerceZoom(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, MinZoom, MaxZoom) : 1.0;

    private static DependencyProperty RegisterImage(string name) => DependencyProperty.Register(
        name, typeof(ImageSource), typeof(BeforeAfterViewer), new FrameworkPropertyMetadata(null, OnImageChanged));

    private static DependencyProperty RegisterText(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(BeforeAfterViewer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(BeforeAfterViewer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
