using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Carbonado.UI.Brand;
using Carbonado.UI.Effects;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The scene's film: an opaque obsidian base with a vignette and a faint pressure bloom, animated film grain
/// (<see cref="GrainEffect"/>) over everything it hosts, and the camera (micro shake and zoom around the core).
/// The base overdraws its bounds so camera moves never reveal what lies behind the overlay.
/// </summary>
internal sealed class FilmGrainLayer : Grid
{
    private const double Overscan = 0.12;

    private GrainEffect? _grain;
    private IntroLayout _layout;
    private double _bloom = -1;

    public FilmGrainLayer()
    {
        Focusable = false;
    }

    /// <summary>Pixel-shader grain allowed (hardware tier; off in the software fallback).</summary>
    public bool UseEffects { get; set; } = true;

    /// <summary>Applies grain, camera and base for the frame.</summary>
    public void Render(IntroState state, in IntroLayout layout)
    {
        double bloom = Math.Round(Math.Max(state.CoreHeat, state.DiamondGlow * 0.6), 2);
        if (layout.Width != _layout.Width || layout.Height != _layout.Height || bloom != _bloom)
        {
            _layout = layout;
            _bloom = bloom;
            InvalidateVisual();
        }

        if (UseEffects && state.Grain > 0.002 && !layout.IsEmpty)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _grain ??= new GrainEffect();
            _grain.Amount = state.Grain;
            _grain.Time = state.Time;
            _grain.Size = new Size(Math.Max(1, layout.Width * dpi.DpiScaleX), Math.Max(1, layout.Height * dpi.DpiScaleY));
            Effect = _grain;
        }
        else
        {
            Effect = null;
        }

        // Camera: zoom around the core plus a two-octave deterministic shake.
        double t = state.Time;
        double amplitude = state.Shake * 6 * layout.Scale;
        double shakeX = amplitude * ((0.6 * Math.Sin((t * 53.1) + 0.3)) + (0.4 * Math.Sin((t * 97.3) + 1.7)));
        double shakeY = amplitude * ((0.6 * Math.Sin((t * 61.7) + 2.1)) + (0.4 * Math.Sin((t * 89.9) + 0.9)));
        double zoom = state.Zoom;
        var camera = new MatrixTransform(
            zoom, 0, 0, zoom,
            (layout.Core.X * (1 - zoom)) + shakeX,
            (layout.Core.Y * (1 - zoom)) + shakeY);
        camera.Freeze();
        RenderTransform = camera;
    }

    protected override void OnRender(DrawingContext dc)
    {
        IntroLayout layout = _layout;
        if (layout.IsEmpty)
        {
            return;
        }

        BrandPalette p = BrandPalette.Current;
        var bounds = new Rect(0, 0, layout.Width, layout.Height);
        Rect overscan = bounds;
        overscan.Inflate(layout.Width * Overscan, layout.Height * Overscan);
        dc.DrawRectangle(new SolidColorBrush(p.Obsidian950), null, overscan);

        // Lifted center (obsidian 850 → 950), tinted iris as pressure builds or the diamond glows.
        double radius = layout.Reach * layout.Scale;
        var lift = new RadialGradientBrush(
            [
                new GradientStop(BrandPalette.Mix(p.Obsidian850, p.Iris, 0.08 * _bloom), 0),
                new GradientStop(BrandPalette.Mix(p.Obsidian900, p.Iris, 0.04 * _bloom), 0.35),
                new GradientStop(p.Obsidian950, 0.8),
            ])
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = layout.Core,
            GradientOrigin = layout.Core,
            RadiusX = radius,
            RadiusY = radius,
        };
        lift.Freeze();
        dc.DrawRectangle(lift, null, overscan);

        // Vignette: corners fall to black.
        var vignette = new RadialGradientBrush(
            [
                new GradientStop(p.Transparent, 0.45),
                new GradientStop(BrandPalette.WithAlpha(p.Shade, 0.55), 0.85),
                new GradientStop(p.Shade, 1.15),
            ])
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = layout.Core,
            GradientOrigin = layout.Core,
            RadiusX = radius,
            RadiusY = radius,
        };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, overscan);
    }
}
