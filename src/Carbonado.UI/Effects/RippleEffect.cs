using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Carbonado.UI.Effects;

/// <summary>Expanding shockwave ring that refracts the content beneath it.</summary>
public sealed class RippleEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(RippleEffect), 0);

    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
        nameof(Center), typeof(Point), typeof(RippleEffect), new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(RippleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(
        nameof(Amplitude), typeof(double), typeof(RippleEffect), new UIPropertyMetadata(0.02, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RippleEffect), new UIPropertyMetadata(0.06, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(RippleEffect), new UIPropertyMetadata(16.0 / 9.0, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight), typeof(double), typeof(RippleEffect), new UIPropertyMetadata(0.6, PixelShaderConstantCallback(5)));

    private static readonly Lazy<PixelShader> s_shader = new(() => EffectSupport.Load("Ripple.ps"));

    public RippleEffect()
    {
        PixelShader = s_shader.Value;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(CenterProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(AmplitudeProperty);
        UpdateShaderValue(ThicknessProperty);
        UpdateShaderValue(AspectRatioProperty);
        UpdateShaderValue(HighlightProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>Ring center in relative (0..1) coordinates.</summary>
    public Point Center
    {
        get => (Point)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    /// <summary>Ring radius in relative horizontal units (0..~1.2).</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>Maximum displacement in relative units (0.02 is strong).</summary>
    public double Amplitude
    {
        get => (double)GetValue(AmplitudeProperty);
        set => SetValue(AmplitudeProperty, value);
    }

    /// <summary>Ring half-width in relative units.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>Element width / height, so the ring stays circular.</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>Rim brightening 0..1.</summary>
    public double Highlight
    {
        get => (double)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new RippleEffect();
}
