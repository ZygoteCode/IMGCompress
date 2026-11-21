using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Carbonado.UI.Effects;

/// <summary>Radial RGB split (prismatic dispersion). <see cref="Amount"/> 0 = off, 1 = strong.</summary>
public sealed class ChromaticAberrationEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(ChromaticAberrationEffect), 0);

    public static readonly DependencyProperty AmountProperty = DependencyProperty.Register(
        nameof(Amount), typeof(double), typeof(ChromaticAberrationEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
        nameof(Center), typeof(Point), typeof(ChromaticAberrationEffect), new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(1)));

    private static readonly Lazy<PixelShader> s_shader = new(() => EffectSupport.Load("ChromaticAberration.ps"));

    public ChromaticAberrationEffect()
    {
        PixelShader = s_shader.Value;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(AmountProperty);
        UpdateShaderValue(CenterProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double Amount
    {
        get => (double)GetValue(AmountProperty);
        set => SetValue(AmountProperty, value);
    }

    /// <summary>Dispersion origin in relative (0..1) coordinates.</summary>
    public Point Center
    {
        get => (Point)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new ChromaticAberrationEffect();
}
