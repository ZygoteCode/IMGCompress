using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Brand;

/// <summary>
/// Brand colors and display font resolved from <c>Theme/Tokens.xaml</c> (the only home of literal colors), for
/// code-drawn brand visuals. Values come from the application resources when the calling thread owns them, else from
/// a private load of the token dictionary; they are captured once as immutable values (thread-agnostic).
/// </summary>
internal sealed class BrandPalette
{
    private const string TokensUri = "pack://application:,,,/Carbonado.UI;component/Theme/Tokens.xaml";

    private static readonly Lazy<BrandPalette> s_current = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private BrandPalette(Func<string, object?> resolve)
    {
        Color C(string key) => resolve(key) is Color color
            ? color
            : throw new InvalidOperationException($"Brand token '{key}' is missing from Tokens.xaml.");

        Obsidian950 = C("C.Obsidian950");
        Obsidian900 = C("C.Obsidian900");
        Obsidian850 = C("C.Obsidian850");
        Obsidian800 = C("C.Obsidian800");
        Obsidian700 = C("C.Obsidian700");
        Diamond50 = C("C.Diamond50");
        Mist300 = C("C.Mist300");
        Ice = C("C.Ice");
        Aqua = C("C.Aqua");
        Iris = C("C.Iris");
        Orchid = C("C.Orchid");
        Rose = C("C.Rose");
        Ember = C("C.Ember");
        Transparent = C("C.Transparent");
        Shade = C("C.Black.A70");
        Spectrum = [Ice, Aqua, Iris, Orchid, Rose, Ember];
        DisplayFont = resolve("Font.Display") as FontFamily ?? throw new InvalidOperationException("Brand token 'Font.Display' is missing.");
    }

    /// <summary>Palette of the running process (resolved on first use).</summary>
    public static BrandPalette Current => s_current.Value;

    public Color Obsidian950 { get; }

    public Color Obsidian900 { get; }

    public Color Obsidian850 { get; }

    public Color Obsidian800 { get; }

    public Color Obsidian700 { get; }

    public Color Diamond50 { get; }

    public Color Mist300 { get; }

    public Color Ice { get; }

    public Color Aqua { get; }

    public Color Iris { get; }

    public Color Orchid { get; }

    public Color Rose { get; }

    public Color Ember { get; }

    public Color Transparent { get; }

    /// <summary>Translucent black used for vignettes and depth (token C.Black.A70).</summary>
    public Color Shade { get; }

    /// <summary>Display typeface family (Inter with system fallbacks).</summary>
    public FontFamily DisplayFont { get; }

    /// <summary>Spectrum (dispersion) sequence Ice → Aqua → Iris → Orchid → Rose → Ember.</summary>
    public IReadOnlyList<Color> Spectrum { get; }

    /// <summary><paramref name="color"/> with its alpha multiplied by <paramref name="alpha"/> (clamped to [0, 1]).</summary>
    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

    /// <summary>Linear blend of two colors (all channels), t clamped to [0, 1].</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.A + ((b.A - a.A) * t)),
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));
    }

    /// <summary>Opaque color with RGB scaled by <paramref name="intensity"/> (light intensities; clamped to [0, 1]).</summary>
    public static Color Intensity(Color color, double intensity)
    {
        double k = Math.Clamp(intensity, 0, 1);
        return Color.FromRgb((byte)Math.Round(color.R * k), (byte)Math.Round(color.G * k), (byte)Math.Round(color.B * k));
    }

    private static BrandPalette Load()
    {
        if (Application.Current is { } app && app.CheckAccess() && app.TryFindResource("C.Obsidian950") is Color)
        {
            return new BrandPalette(app.TryFindResource);
        }

        var tokens = new ResourceDictionary { Source = new Uri(TokensUri, UriKind.Absolute) };
        return new BrandPalette(key => tokens.Contains(key) ? tokens[key] : null);
    }
}
