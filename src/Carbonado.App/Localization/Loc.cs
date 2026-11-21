using System.Globalization;
using System.Windows;

namespace Carbonado.App.Localization;

/// <summary>
/// Runtime language switching. String tables are ResourceDictionaries (Strings.en.xaml / Strings.it.xaml) whose
/// keys start with "L."; XAML binds them with DynamicResource, code uses <see cref="Get"/> / <see cref="Format"/>.
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<string> Supported = ["it", "en"];

    private static ResourceDictionary? s_current;
    private static Func<string, string?>? s_lookupOverride;

    public static event EventHandler? LanguageChanged;

    /// <summary>"it" or "en".</summary>
    public static string Language { get; private set; } = "en";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Resolves "auto" to the Windows UI language when supported, else English.</summary>
    public static string Resolve(string? preference)
    {
        if (preference is "it" or "en")
        {
            return preference;
        }

        string ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Supported.Contains(ui) ? ui : "en";
    }

    /// <summary>Loads the string table for <paramref name="language"/> into the application resources.</summary>
    public static void Apply(Application app, string language)
    {
        ArgumentNullException.ThrowIfNull(app);
        language = Resolve(language);
        var dict = Load(language);
        var merged = app.Resources.MergedDictionaries;
        if (s_current is not null)
        {
            int index = merged.IndexOf(s_current);
            if (index >= 0)
            {
                merged[index] = dict;
            }
            else
            {
                merged.Add(dict);
            }
        }
        else
        {
            merged.Add(dict);
        }

        s_current = dict;
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == "it" ? "it-IT" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Loads a string table without applying it.</summary>
    public static ResourceDictionary Load(string language) =>
        new() { Source = new Uri($"pack://application:,,,/Carbonado;component/Localization/Strings.{language}.xaml", UriKind.Absolute) };

    /// <summary>Localized string for <paramref name="key"/> ("L.*"); returns the key itself when missing.</summary>
    public static string Get(string key)
    {
        if (s_lookupOverride?.Invoke(key) is { } overridden)
        {
            return overridden;
        }

        if (s_current is not null && s_current.Contains(key) && s_current[key] is string value)
        {
            return value;
        }

        return Application.Current?.TryFindResource(key) as string ?? key;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    /// <summary>Test hook: resolves strings without WPF resources.</summary>
    internal static void SetLookupOverride(Func<string, string?>? lookup) => s_lookupOverride = lookup;
}
