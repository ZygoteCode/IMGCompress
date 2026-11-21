using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Carbonado.UI.Motion;

namespace Carbonado.UI.Controls;

/// <summary>
/// Ambient carbon dust: <see cref="Count"/> tiny prismatic motes drift, twinkle and — when an
/// <see cref="Attractor"/> is set (relative 0..1 coordinates, e.g. the drop point) — are drawn toward it.
/// The simulation is deterministic for a <see cref="Seed"/> (fixed 60 Hz steps), runs only while visible and
/// <see cref="IsRunning"/>, freezes under reduced motion and rests with <see cref="AmbientMotion"/> (app inactive or
/// user idle). It redraws at most <see cref="FramesPerSecond"/> times a second from a timer instead of every
/// composition frame: slow dust needs no 199 Hz redraws. Colors come from <see cref="Palette"/> (tokens).
/// </summary>
public sealed class ParticleField : FrameworkElement
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(ParticleField),
        new FrameworkPropertyMetadata(90, OnPopulationChanged, (d, v) => Math.Clamp((int)v, 0, 2000)));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(ParticleField),
        new FrameworkPropertyMetadata(0.6, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (d, v) => v is double x && double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0.0));

    public static readonly DependencyProperty AttractorProperty = DependencyProperty.Register(
        nameof(Attractor), typeof(Point?), typeof(ParticleField), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(ParticleField), new FrameworkPropertyMetadata(true, OnRunInputChanged));

    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(ParticleField), new FrameworkPropertyMetadata(7, OnPopulationChanged));

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette), typeof(IList<Brush>), typeof(ParticleField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPaletteChanged));

    /// <summary>Redraw rate of the ambient dust (the simulation itself still steps at 60 Hz).</summary>
    public const double FramesPerSecond = 30;

    private const double StepSeconds = 1.0 / 60;
    private const int AlphaLevels = 16;

    private readonly Stopwatch _clock = new();
    private Particle[] _particles = [];
    private SolidColorBrush[][] _brushes = [];
    private double _time;
    private double _pending;
    private bool _subscribed;
    private DispatcherTimer? _timer;

    public ParticleField()
    {
        Populate();
        Loaded += (_, _) =>
        {
            AmbientMotion.EnsureAttached();
            MotionSettings.Changed -= OnMotionChanged;
            MotionSettings.Changed += OnMotionChanged;
            AmbientMotion.Changed -= OnMotionChanged;
            AmbientMotion.Changed += OnMotionChanged;
            UpdateRunning();
        };
        Unloaded += (_, _) =>
        {
            MotionSettings.Changed -= OnMotionChanged;
            AmbientMotion.Changed -= OnMotionChanged;
            UpdateRunning();
        };
        IsVisibleChanged += (_, _) => UpdateRunning();
    }

    /// <summary>Number of motes (0…2000).</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Overall brightness and attractor pull, 0…1.</summary>
    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    /// <summary>Point (relative 0..1) the motes gather toward; null lets them drift freely.</summary>
    public Point? Attractor
    {
        get => (Point?)GetValue(AttractorProperty);
        set => SetValue(AttractorProperty, value);
    }

    /// <summary>Animates while true (and loaded, visible, motion allowed); false freezes the dust.</summary>
    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    /// <summary>Seed of the deterministic layout and motion.</summary>
    public int Seed
    {
        get => (int)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>Solid brushes the motes are tinted with (weighted toward the first entries).</summary>
    public IList<Brush>? Palette
    {
        get => (IList<Brush>?)GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    /// <summary>Simulated seconds since the layout was seeded.</summary>
    internal double Time => _time;

    /// <summary>Current mote positions in relative coordinates.</summary>
    internal IReadOnlyList<Point> Positions => _particles.Select(p => new Point(p.X, p.Y)).ToArray();

    /// <summary>Advances the simulation by <paramref name="span"/> in fixed 60 Hz steps (deterministic).</summary>
    internal void Advance(TimeSpan span)
    {
        _pending += span.TotalSeconds;
        while (_pending >= StepSeconds)
        {
            Step();
            _pending -= StepSeconds;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (_brushes.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        double intensity = Intensity;
        foreach (ref readonly var p in _particles.AsSpan())
        {
            double twinkle = 0.65 + (0.35 * Math.Sin((_time * p.Twinkle) + p.Phase));
            int level = (int)Math.Round(p.Alpha * intensity * twinkle * AlphaLevels);
            if (level <= 0)
            {
                continue;
            }

            var brushes = _brushes[p.Color % _brushes.Length];
            var center = new Point(p.X * width, p.Y * height);
            if (p.Size > 1.6)
            {
                // Larger motes carry a faint bloom.
                drawingContext.DrawEllipse(brushes[Math.Max(1, level / 5)], null, center, p.Size * 2.2, p.Size * 2.2);
            }

            drawingContext.DrawEllipse(brushes[Math.Min(level, AlphaLevels)], null, center, p.Size / 2, p.Size / 2);
        }
    }

    private void Populate()
    {
        var rng = new SplitMix(Seed);
        _particles = new Particle[Count];
        for (int i = 0; i < _particles.Length; i++)
        {
            double angle = rng.Next() * Math.PI * 2;
            double speed = 0.004 + (rng.Next() * 0.014);
            double color = rng.Next();
            _particles[i] = new Particle
            {
                X = rng.Next(),
                Y = rng.Next(),
                Vx = Math.Cos(angle) * speed,
                Vy = (Math.Sin(angle) * speed) - 0.006,
                Size = 0.6 + (Math.Pow(rng.Next(), 3) * 1.8),
                Alpha = 0.3 + (rng.Next() * 0.7),
                Phase = rng.Next() * Math.PI * 2,
                Twinkle = 0.6 + (rng.Next() * 2.2),
                Color = color < 0.45 ? 0 : color < 0.7 ? 1 : color < 0.85 ? 2 : color < 0.95 ? 3 : 4,
            };
        }

        _time = 0;
        _pending = 0;
        InvalidateVisual();
    }

    private void Step()
    {
        _time += StepSeconds;
        var attractor = Attractor;
        double pull = 0.9 * Intensity;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (attractor is { } a)
            {
                p.Vx += (a.X - p.X) * pull * StepSeconds;
                p.Vy += (a.Y - p.Y) * pull * StepSeconds;
                p.Vx *= 0.985;
                p.Vy *= 0.985;
            }

            // Gentle turbulence keeps the dust alive without randomness per frame.
            p.X += (p.Vx + (0.004 * Math.Sin((_time * 0.7) + p.Phase))) * StepSeconds;
            p.Y += (p.Vy + (0.004 * Math.Cos((_time * 0.5) + p.Phase))) * StepSeconds;
            p.X = Wrap(p.X);
            p.Y = Wrap(p.Y);
        }
    }

    private static double Wrap(double v) => v < -0.02 ? v + 1.04 : v > 1.02 ? v - 1.04 : v;

    private void UpdateRunning()
    {
        bool run = IsRunning && IsLoaded && IsVisible && !MotionSettings.ReducedMotion && AmbientMotion.IsActive;
        if (run && !_subscribed)
        {
            _subscribed = true;
            _clock.Restart();
            _timer ??= CreateTimer();
            _timer.Start();
        }
        else if (!run && _subscribed)
        {
            _subscribed = false;
            _clock.Stop();
            _timer?.Stop();
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateRunning);

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromSeconds(1 / FramesPerSecond) };
        timer.Tick += OnTick;
        return timer;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Clamp long gaps (debugger, busy UI thread) so the dust never jumps.
        var elapsed = TimeSpan.FromTicks(Math.Min(_clock.Elapsed.Ticks, TimeSpan.FromMilliseconds(100).Ticks));
        _clock.Restart();
        Advance(elapsed * MotionSettings.Speed);
    }

    private void BuildBrushes()
    {
        var palette = Palette;
        if (palette is null || palette.Count == 0)
        {
            _brushes = [];
            return;
        }

        _brushes = palette.Select(source =>
        {
            var color = source is SolidColorBrush solid ? solid.Color : default;
            var levels = new SolidColorBrush[AlphaLevels + 1];
            for (int level = 0; level <= AlphaLevels; level++)
            {
                levels[level] = new SolidColorBrush(color) { Opacity = (double)level / AlphaLevels };
                levels[level].Freeze();
            }

            return levels;
        }).ToArray();
    }

    private static void OnPopulationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ParticleField)d).Populate();

    private static void OnRunInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ParticleField)d).UpdateRunning();

    private static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ParticleField)d).BuildBrushes();

    private struct Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public double Size;
        public double Alpha;
        public double Phase;
        public double Twinkle;
        public int Color;
    }

    /// <summary>SplitMix64: tiny, fast, identical on every runtime (System.Random's seeded sequence is not a contract).</summary>
    private struct SplitMix(int seed)
    {
        private ulong _state = (ulong)seed * 0x9E3779B97F4A7C15UL;

        /// <summary>Next uniform double in [0, 1).</summary>
        public double Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
