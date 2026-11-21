using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Carbonado.UI.Effects;

/// <summary>Helpers for Carbonado's pixel shaders.</summary>
public static class EffectSupport
{
    /// <summary>True when WPF renders with full hardware acceleration (tier 2) — shaders run on the GPU.</summary>
    public static bool IsHardwareAccelerated => (RenderCapability.Tier >> 16) >= 2;

    /// <summary>Loads a compiled shader shipped in Carbonado.UI/Effects as a frozen, thread-shareable PixelShader.</summary>
    internal static PixelShader Load(string fileName)
    {
        var shader = new PixelShader
        {
            UriSource = new Uri($"pack://application:,,,/Carbonado.UI;component/Effects/{fileName}", UriKind.Absolute),
        };
        shader.Freeze();
        return shader;
    }
}
