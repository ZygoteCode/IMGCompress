using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Carbonado.UI.Effects;

/// <summary>Animated film grain. Animate <see cref="Time"/> every frame; keep <see cref="Amount"/> subtle (≈0.05).</summary>
public sealed class GrainEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(GrainEffect), 0);

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(GrainEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty AmountProperty = DependencyProperty.Register(
        nameof(Amount), typeof(double), typeof(GrainEffect), new UIPropertyMetadata(0.05, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(Size), typeof(GrainEffect), new UIPropertyMetadata(new Size(1280, 800), PixelShaderConstantCallback(2)));

    private static readonly Lazy<PixelShader> s_shader = new(() => EffectSupport.Load("Grain.ps"));

    public GrainEffect()
    {
        PixelShader = s_shader.Value;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(AmountProperty);
        UpdateShaderValue(SizeProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public double Amount
    {
        get => (double)GetValue(AmountProperty);
        set => SetValue(AmountProperty, value);
    }

    /// <summary>Element size in pixels; one grain cell per pixel.</summary>
    public Size Size
    {
        get => (Size)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new GrainEffect();
}
