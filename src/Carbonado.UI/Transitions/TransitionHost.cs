using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Carbonado.UI.Effects;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Transitions;

/// <summary>
/// Content host with brand page transitions (spec §4.11). On every <see cref="ContentControl.Content"/> change the
/// outgoing page is captured as a bitmap that drifts −8 px and fades in 180 ms while dispersing (chromatic
/// aberration 0 → 0.6); the incoming page rises 12 px, scales 0.985 → 1 and sharpens from an 8 px blur in 420 ms
/// (Crystallize), then its <see cref="Stagger"/> children enter in order. Shaders run only on tier-2 hardware;
/// reduced motion collapses everything to a short crossfade. Works without a theme (built-in template).
/// </summary>
[TemplatePart(Name = ContentPartName, Type = typeof(ContentPresenter))]
[TemplatePart(Name = SnapshotPartName, Type = typeof(Image))]
public class TransitionHost : ContentControl
{
    public static readonly DependencyProperty TransitionProperty = DependencyProperty.Register(
        nameof(Transition), typeof(TransitionKind), typeof(TransitionHost), new PropertyMetadata(TransitionKind.Refraction));

    internal const double OutDrift = -8;
    internal const double InRise = 12;
    internal static readonly TimeSpan OutTime = TimeSpan.FromMilliseconds(180);
    internal static readonly TimeSpan InTime = TimeSpan.FromMilliseconds(420);
    internal static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(240);

    private const string ContentPartName = "PART_Content";
    private const string SnapshotPartName = "PART_Snapshot";
    private const double InScale = 0.985;
    private const double InBlur = 8;
    private const double OutAberration = 0.6;

    private ContentPresenter? _content;
    private Image? _snapshot;
    private int _generation;

    static TransitionHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TransitionHost), new FrameworkPropertyMetadata(typeof(TransitionHost)));
        TemplateProperty.OverrideMetadata(typeof(TransitionHost), new FrameworkPropertyMetadata(CreateTemplate()));
        FocusableProperty.OverrideMetadata(typeof(TransitionHost), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Animation used for the next content swap.</summary>
    public TransitionKind Transition
    {
        get => (TransitionKind)GetValue(TransitionProperty);
        set => SetValue(TransitionProperty, value);
    }

    /// <summary>True while a swap animation is running.</summary>
    internal bool IsTransitioning { get; private set; }

    /// <summary>What the last swap animated (diagnostics and tests).</summary>
    internal TransitionPlan? LastPlan { get; private set; }

    internal ContentPresenter? ContentPart => _content;

    internal Image? SnapshotPart => _snapshot;

    public override void OnApplyTemplate()
    {
        Finish();
        base.OnApplyTemplate();
        _content = GetTemplateChild(ContentPartName) as ContentPresenter;
        _snapshot = GetTemplateChild(SnapshotPartName) as Image;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        // Runs before the presenter is told about the new content, so it still shows the outgoing page.
        base.OnContentChanged(oldContent, newContent);
        if (_content is null || !IsLoaded)
        {
            return;
        }

        Finish();
        _generation++;
        var plan = TransitionPlan.For(Transition);
        LastPlan = plan;
        if (plan is null)
        {
            return;
        }

        var outgoing = oldContent is null ? null : Capture(_content);
        Begin(plan, outgoing);
    }

    private void Begin(TransitionPlan plan, BitmapSource? outgoing)
    {
        if (_content is null)
        {
            return;
        }

        int generation = _generation;
        IsTransitioning = true;
        if (outgoing is not null && _snapshot is not null)
        {
            ShowOutgoing(_snapshot, outgoing, plan);
        }

        var ease = plan.Kind == TransitionKind.Refraction ? Eases.Crystallize : Eases.Glide;
        var fadeIn = new DoubleAnimation(0, 1, plan.InDuration) { EasingFunction = Eases.Glide, FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) =>
        {
            if (generation == _generation)
            {
                Finish();
            }
        };
        _content.BeginAnimation(OpacityProperty, fadeIn);

        if (plan.Rise != 0)
        {
            var scale = new ScaleTransform();
            var rise = new TranslateTransform();
            _content.RenderTransformOrigin = new Point(0.5, 0.5);
            _content.RenderTransform = new TransformGroup { Children = { scale, rise } };
            rise.BeginAnimation(TranslateTransform.YProperty, Tween(plan.Rise, 0, plan.InDuration, ease));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(InScale, 1, plan.InDuration, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(InScale, 1, plan.InDuration, ease));
        }

        if (plan.Blur)
        {
            var blur = new BlurEffect { Radius = InBlur, RenderingBias = RenderingBias.Performance };
            _content.Effect = blur;
            blur.BeginAnimation(BlurEffect.RadiusProperty, Tween(InBlur, 0, plan.InDuration, ease));
        }

        // The presenter builds the new page during the next layout pass; reveal its staggered children right after.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation == _generation && _content is not null)
            {
                Stagger.Reveal(_content);
            }
        }));
    }

    private void ShowOutgoing(Image snapshot, BitmapSource outgoing, TransitionPlan plan)
    {
        snapshot.Source = outgoing;
        snapshot.Width = _content!.ActualWidth;
        snapshot.Height = _content.ActualHeight;
        snapshot.Visibility = Visibility.Visible;
        snapshot.BeginAnimation(OpacityProperty, Tween(1, 0, plan.OutDuration, Eases.Glide));
        if (plan.Drift != 0)
        {
            var drift = new TranslateTransform();
            snapshot.RenderTransform = drift;
            drift.BeginAnimation(TranslateTransform.YProperty, Tween(0, plan.Drift, plan.OutDuration, Eases.Glide));
        }

        if (plan.Aberration)
        {
            var dispersion = new ChromaticAberrationEffect();
            snapshot.Effect = dispersion;
            dispersion.BeginAnimation(ChromaticAberrationEffect.AmountProperty, Tween(0, OutAberration, plan.OutDuration, Eases.Glide));
        }
    }

    /// <summary>Ends any running swap: drops the snapshot, effects and transforms; the new page stays.</summary>
    private void Finish()
    {
        if (_snapshot is not null)
        {
            _snapshot.BeginAnimation(OpacityProperty, null);
            _snapshot.Source = null;
            _snapshot.Effect = null;
            _snapshot.RenderTransform = Transform.Identity;
            _snapshot.Visibility = Visibility.Collapsed;
        }

        if (_content is not null)
        {
            _content.BeginAnimation(OpacityProperty, null);
            _content.Effect = null;
            _content.RenderTransform = Transform.Identity;
        }

        IsTransitioning = false;
    }

    /// <summary>Renders the presenter (still showing the outgoing page) to a device-resolution bitmap.</summary>
    private BitmapSource? Capture(FrameworkElement source)
    {
        double width = source.ActualWidth;
        double height = source.ActualHeight;
        if (width < 1 || height < 1)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(source)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static DoubleAnimation Tween(double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        animation.Freeze();
        return animation;
    }

    private static ControlTemplate CreateTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter), ContentPartName);
        content.SetValue(MarginProperty, new TemplateBindingExtension(PaddingProperty));
        root.AppendChild(content);

        var snapshot = new FrameworkElementFactory(typeof(Image), SnapshotPartName);
        snapshot.SetValue(MarginProperty, new TemplateBindingExtension(PaddingProperty));
        snapshot.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        snapshot.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        snapshot.SetValue(Image.StretchProperty, Stretch.Fill);
        snapshot.SetValue(IsHitTestVisibleProperty, false);
        snapshot.SetValue(VisibilityProperty, Visibility.Collapsed);
        root.AppendChild(snapshot);

        var template = new ControlTemplate(typeof(TransitionHost)) { VisualTree = root };
        template.Seal();
        return template;
    }
}

/// <summary>What a content swap animates; <see cref="IsFadeOnly"/> under reduced motion.</summary>
internal sealed record TransitionPlan(
    TransitionKind Kind, TimeSpan OutDuration, TimeSpan InDuration, double Drift, double Rise, bool Blur, bool Aberration)
{
    public bool IsFadeOnly => Drift == 0 && Rise == 0 && !Blur && !Aberration;

    /// <summary>The plan for <paramref name="kind"/> under the current motion settings; null = instant swap.</summary>
    public static TransitionPlan? For(TransitionKind kind)
    {
        if (kind == TransitionKind.None)
        {
            return null;
        }

        var outTime = MotionSettings.Scale(TransitionHost.OutTime);
        var inTime = MotionSettings.Scale(kind == TransitionKind.Fade ? TransitionHost.FadeInTime : TransitionHost.InTime);
        if (MotionSettings.ReducedMotion || kind == TransitionKind.Fade)
        {
            return new TransitionPlan(kind, outTime, inTime, 0, 0, false, false);
        }

        bool shaders = EffectSupport.IsHardwareAccelerated;
        return new TransitionPlan(kind, outTime, inTime, TransitionHost.OutDrift, TransitionHost.InRise, shaders, shaders);
    }
}
