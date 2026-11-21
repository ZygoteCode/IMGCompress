using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Carbonado.UI.Brand;
using Carbonado.UI.Effects;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The Carbonado startup intro (spec §4.10): an overlay that only animates — the shell is built and loads data
/// behind it, and nothing waits for it. Place it above the shell content, set <see cref="LogoTarget"/> to the
/// title-bar <see cref="DiamondMark"/>, call <see cref="Play"/> once the window is shown and remove (or ignore) it on
/// <see cref="Completed"/>; it collapses itself and stops intercepting input from the hand-off on. Click, Esc or
/// Space skip (until the hand-off). If rendering a frame ever throws, the intro ends at once — collapsed,
/// <see cref="Completed"/> raised — before the exception propagates, so it can never stay stuck over the app.
/// <see cref="IntroMode.Cinematic"/> becomes <see cref="IntroMode.Short"/> under reduced motion.
/// Every frame is a pure function of time: <see cref="RenderAt"/> renders any instant (snapshot tests).
/// </summary>
public sealed class IntroOverlay : Control
{
    /// <summary>Identifies the <see cref="Mode"/> dependency property.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(IntroMode), typeof(IntroOverlay), new PropertyMetadata(IntroMode.Cinematic));

    /// <summary>Identifies the <see cref="LogoTarget"/> dependency property.</summary>
    public static readonly DependencyProperty LogoTargetProperty = DependencyProperty.Register(
        nameof(LogoTarget), typeof(FrameworkElement), typeof(IntroOverlay), new PropertyMetadata(null, OnLogoTargetChanged));

    /// <summary>Identifies the <see cref="Tagline"/> dependency property.</summary>
    public static readonly DependencyProperty TaglineProperty = DependencyProperty.Register(
        nameof(Tagline), typeof(string), typeof(IntroOverlay), new PropertyMetadata(null, OnTaglineChanged));

    /// <summary>Localization key the tagline follows by default (dynamic resource).</summary>
    public const string TaglineResourceKey = "L.Intro.Tagline";

    private readonly Grid _root;
    private readonly FilmGrainLayer _film;
    private readonly Grid _refraction;
    private readonly LightRays _rays;
    private readonly ImplosionField _field;
    private readonly Shockwave _shockwave;
    private readonly bool _effects;
    private Diamond3D? _diamond;
    private WordmarkReveal? _wordmark;
    private ChromaticAberrationEffect? _aberration;

    private IntroState _state = new();
    private IntroLayout _layout;
    private CinematicClock? _clock;
    private (IntroMode Mode, bool FadeOnly) _built;
    private bool _framed;
    private bool _resumeOnLoad;
    private Window? _keyWindow;
    private LogoLease? _lease;

    static IntroOverlay()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IntroOverlay), new FrameworkPropertyMetadata(typeof(IntroOverlay)));
        FocusableProperty.OverrideMetadata(typeof(IntroOverlay), new FrameworkPropertyMetadata(false));
    }

    /// <summary>
    /// Creates the overlay with only its cheap layers (the first frames are the obsidian void) and starts preparing
    /// the heavy assets on the thread pool. Tagline follows <see cref="TaglineResourceKey"/>.
    /// </summary>
    public IntroOverlay()
    {
        IntroAssets.PrepareInBackground();
        _effects = EffectSupport.IsHardwareAccelerated;
        _rays = new LightRays();
        _field = new ImplosionField();
        var distortion = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        distortion.Children.Add(_rays);
        distortion.Children.Add(_field);
        _shockwave = new Shockwave(distortion) { UseEffects = _effects };
        _refraction = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        _refraction.Children.Add(distortion);
        _refraction.Children.Add(_shockwave);
        _film = new FilmGrainLayer { UseEffects = _effects };
        _film.Children.Add(_refraction);
        _root = new Grid();
        _root.Children.Add(_film);
        AddVisualChild(_root);
        AddLogicalChild(_root);

        AutomationProperties.SetAutomationId(this, nameof(IntroOverlay));
        SetResourceReference(TaglineProperty, TaglineResourceKey);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Raised once the intro reaches its end or is skipped; the overlay is collapsed by then.</summary>
    public event EventHandler? Completed;

    /// <summary>Raised when the hand-off begins (or is skipped past): the moment to reveal the shell's staggered content.</summary>
    public event EventHandler? HandOffStarted;

    /// <summary>Requested intro mode (default <see cref="IntroMode.Cinematic"/>); applied at the next <see cref="Play"/>.</summary>
    public IntroMode Mode
    {
        get => (IntroMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// The logo slot the diamond flies to (typically the title-bar <see cref="DiamondMark"/>). The overlay hides it
    /// until the diamond lands, cross-fades it in (with a glint when it is a <see cref="DiamondMark"/>) and restores
    /// its own opacity and glint at the end.
    /// </summary>
    public FrameworkElement? LogoTarget
    {
        get => (FrameworkElement?)GetValue(LogoTargetProperty);
        set => SetValue(LogoTargetProperty, value);
    }

    /// <summary>Tagline typed under the wordmark; follows <see cref="TaglineResourceKey"/> unless set.</summary>
    public string? Tagline
    {
        get => (string?)GetValue(TaglineProperty);
        set => SetValue(TaglineProperty, value);
    }

    /// <summary>The mode that actually plays: <see cref="IntroMode.Short"/> replaces Cinematic under reduced motion.</summary>
    public IntroMode EffectiveMode => Effective(Mode, MotionSettings.ReducedMotion);

    /// <summary>Length of the effective intro in seconds (3.4 cinematic, 0.9 short, 0 off).</summary>
    public double Duration => IntroTimelineFactory.DurationOf(EffectiveMode);

    /// <summary>True while the intro is playing.</summary>
    public bool IsPlaying => _clock?.IsRunning == true;

    protected override int VisualChildrenCount => 1;

    protected override IEnumerator LogicalChildren => new object[] { _root }.GetEnumerator();

    /// <summary>
    /// Starts building the intro's geometry (diamond mesh, wordmark) on the thread pool. Optional and idempotent: call it
    /// as early as possible during startup; every overlay also calls it when constructed.
    /// </summary>
    public static void PrepareAssets() => IntroAssets.PrepareInBackground();

    /// <summary>
    /// Starts the intro from the beginning (or resumes it). Off completes immediately. The first frames only need the
    /// obsidian void; the 3D diamond and the wordmark are built right after them, at idle priority.
    /// </summary>
    public void Play()
    {
        CinematicClock clock = EnsureClock();
        Visibility = Visibility.Visible;
        HookKeys();
        clock.Play();
        if (clock.IsRunning && _diamond is null)
        {
            Dispatcher.InvokeAsync(PrepareStage, DispatcherPriority.Background);
        }
    }

    /// <summary>Jumps to the final state at once and raises <see cref="Completed"/> (no-op once completed).</summary>
    public void Skip() => EnsureClock().Skip();

    /// <summary>Sets the whole composition to its state at <paramref name="seconds"/> (clamped to the intro).</summary>
    public void RenderAt(double seconds)
    {
        _framed = true;
        EnsureClock().Seek(seconds);
    }

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _root : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(constraint);
        return default;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _root.Arrange(new Rect(arrangeBounds));
        if (arrangeBounds.Width != _layout.Width || arrangeBounds.Height != _layout.Height)
        {
            _layout = new IntroLayout(arrangeBounds.Width, arrangeBounds.Height);
            if (_framed)
            {
                RenderComposition();
            }
        }

        return arrangeBounds;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsPlaying)
        {
            e.Handled = true;
            Skip();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static IntroMode Effective(IntroMode mode, bool reducedMotion) =>
        mode == IntroMode.Cinematic && reducedMotion ? IntroMode.Short : mode;

    private static void OnLogoTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (IntroOverlay)d;
        overlay._lease?.Restore();
        overlay._lease = null;
        overlay.RefreshIfIdle();
    }

    private static void OnTaglineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((IntroOverlay)d).RefreshIfIdle();

    private void RefreshIfIdle()
    {
        if (_framed && !IsPlaying)
        {
            RenderComposition();
        }
    }

    private CinematicClock EnsureClock()
    {
        bool reduced = MotionSettings.ReducedMotion;
        (IntroMode, bool) wanted = (Effective(Mode, reduced), reduced);
        if (_clock is { } current && (current.IsRunning || wanted == _built))
        {
            return current;
        }

        if (_clock is not null)
        {
            _clock.Completed -= OnClockCompleted;
        }

        _state = new IntroState();
        _built = wanted;
        _clock = new CinematicClock(IntroTimelineFactory.Create(wanted.Item1, wanted.Item2, _state, RenderComposition, OnHandOff));
        _clock.Completed += OnClockCompleted;
        return _clock;
    }

    /// <summary>Applies <see cref="_state"/> to every layer (bound last in the timeline, after all channels).</summary>
    private void RenderComposition()
    {
        _framed = true;
        IntroState s = _state;
        IntroLayout layout = _layout;

        _film.Render(s, layout);
        _film.Opacity = s.SceneOpacity;
        _film.Visibility = s.SceneOpacity > 0.001 ? Visibility.Visible : Visibility.Hidden;
        if (_effects && s.Aberration > 0.004 && !layout.IsEmpty)
        {
            _aberration ??= new ChromaticAberrationEffect();
            _aberration.Amount = s.Aberration;
            _aberration.Center = layout.RelativeCore;
            _refraction.Effect = _aberration;
        }
        else
        {
            _refraction.Effect = null;
        }

        _rays.Render(s, layout);
        _field.Render(s, layout);
        _shockwave.Render(s, layout);
        if (_diamond is null && NeedsStage(s))
        {
            BuildStage();
        }

        RenderStage(s, layout);
        UpdateLogo(s);

        // From the hand-off on, input goes straight to the shell.
        IsHitTestVisible = s.Time < IntroTimelineFactory.HandOffTime(_built.Mode);
    }

    /// <summary>True when the diamond or any text is visible in this frame (never at the end state, e.g. after a skip).</summary>
    private static bool NeedsStage(IntroState s) =>
        (s.DiamondOpacity > 0.002 && s.DiamondScale > 0.001)
        || (s.TextExit < 1 && (s.Tagline > 0 || Array.Exists(s.Letters, static p => p > 0)));

    /// <summary>Idle-time stage build after <see cref="Play"/>, rendered at once in the current state (no default-state flash).</summary>
    private void PrepareStage()
    {
        if (_diamond is null && IsPlaying)
        {
            BuildStage();
            RenderStage(_state, _layout);
        }
    }

    /// <summary>
    /// Builds the diamond and wordmark layers (once). Their frozen assets come from <see cref="IntroAssets"/>, normally
    /// ready by now; the tagline glyphs are laid out here too, so no text or font work lands mid-animation.
    /// </summary>
    private void BuildStage()
    {
        // Built into locals first: a failing constructor leaves no half-built stage behind.
        var diamond = new Diamond3D { UseEffects = _effects };
        var wordmark = new WordmarkReveal { UseEffects = _effects, Tagline = Tagline };
        wordmark.Prepare(_layout);
        _root.Children.Add(diamond);
        _root.Children.Add(wordmark);
        (_diamond, _wordmark) = (diamond, wordmark);
    }

    private void RenderStage(IntroState s, in IntroLayout layout)
    {
        if (_diamond is null || _wordmark is null)
        {
            return;
        }

        _diamond.Target = LogoBounds();
        _diamond.Render(s, layout);
        _wordmark.Tagline = Tagline;
        _wordmark.Render(s, layout);
    }

    private Rect? LogoBounds()
    {
        if (LogoTarget is not { ActualWidth: > 0, ActualHeight: > 0 } target || FindCommonVisualAncestor(target) is null)
        {
            return null;
        }

        try
        {
            return target.TransformToVisual(this).TransformBounds(new Rect(target.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void UpdateLogo(IntroState s)
    {
        if (LogoTarget is not { } target)
        {
            return;
        }

        if (s.Time >= IntroTimelineFactory.DurationOf(_built.Mode))
        {
            _lease?.Restore();
            _lease = null;
            return;
        }

        _lease ??= new LogoLease(target);
        target.Opacity = _lease.Opacity * s.LogoReveal;
        if (target is DiamondMark mark)
        {
            mark.Glint = Math.Max(_lease.Glint, s.LogoGlint);
        }
    }

    private void OnHandOff() => HandOffStarted?.Invoke(this, EventArgs.Empty);

    /// <summary>End of a run (normal, skipped or faulted): get out of the way first, then hand the logo back.</summary>
    private void OnClockCompleted(object? sender, EventArgs e)
    {
        UnhookKeys();
        _resumeOnLoad = false;
        Visibility = Visibility.Collapsed;
        try
        {
            _lease?.Restore();
        }
        finally
        {
            _lease = null;
            Completed?.Invoke(this, EventArgs.Empty);
            ScheduleTrim();
        }
    }

    /// <summary>
    /// The cinematic allocates tens of megabytes (particles, 3D render data, intermediate effect surfaces) that are all
    /// garbage once it ends, but an idle app never triggers a collection: one compacting collection a moment after the
    /// hand-off gives that memory back (measured ≈75 MB of private bytes). Runs once per intro, at idle priority.
    /// </summary>
    private void ScheduleTrim()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        timer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_resumeOnLoad)
        {
            _resumeOnLoad = false;
            Play();
        }
        else if (IsPlaying)
        {
            HookKeys();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_clock?.IsRunning == true)
        {
            _clock.Pause();
            _resumeOnLoad = true;
        }

        UnhookKeys();
    }

    private void HookKeys()
    {
        Window? window = Window.GetWindow(this);
        if (window == _keyWindow)
        {
            return;
        }

        UnhookKeys();
        _keyWindow = window;
        if (window is not null)
        {
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void UnhookKeys()
    {
        if (_keyWindow is not null)
        {
            _keyWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _keyWindow = null;
        }
    }

    /// <summary>Esc and Space skip until the hand-off; from then on the shell is interactive and keys are its own.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsPlaying && IsHitTestVisible && e.Key is Key.Escape or Key.Space)
        {
            e.Handled = true;
            Skip();
        }
    }

    /// <summary>The logo slot's own opacity and glint, held while the intro drives them and restored afterwards.</summary>
    private sealed class LogoLease
    {
        private readonly FrameworkElement _target;
        private readonly object _localOpacity;
        private readonly object? _localGlint;

        public LogoLease(FrameworkElement target)
        {
            _target = target;
            _localOpacity = target.ReadLocalValue(OpacityProperty);
            Opacity = target.Opacity;
            if (target is DiamondMark mark)
            {
                _localGlint = mark.ReadLocalValue(DiamondMark.GlintProperty);
                Glint = mark.Glint;
            }
        }

        public double Opacity { get; }

        public double Glint { get; }

        public void Restore()
        {
            Restore(OpacityProperty, _localOpacity);
            if (_localGlint is not null)
            {
                Restore(DiamondMark.GlintProperty, _localGlint);
            }
        }

        private void Restore(DependencyProperty property, object local)
        {
            if (local == DependencyProperty.UnsetValue)
            {
                _target.ClearValue(property);
            }
            else if (local is BindingExpressionBase binding)
            {
                BindingOperations.SetBinding(_target, property, binding.ParentBindingBase);
            }
            else
            {
                _target.SetValue(property, local);
            }
        }
    }
}
