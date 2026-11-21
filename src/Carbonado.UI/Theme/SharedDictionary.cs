using System.Windows;

namespace Carbonado.UI.Theme;

/// <summary>
/// Resource dictionary that loads its <see cref="Source"/> once per UI thread and merges that shared instance.
/// Each Carbonado theme file merges what it references (Tokens, Base) so its StaticResource lookups resolve
/// inside the file, without re-parsing those dictionaries for every file. <see cref="Source"/> must be absolute
/// or component-rooted (<c>/Carbonado.UI;component/…</c>).
/// </summary>
public sealed class SharedDictionary : ResourceDictionary
{
    [ThreadStatic]
    private static Dictionary<string, ResourceDictionary>? t_loaded;

    private Uri? _source;

    /// <summary>Dictionary to share; setting it merges the per-thread instance (loading it on first use).</summary>
    public new Uri? Source
    {
        get => _source;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_source is not null)
            {
                throw new InvalidOperationException("SharedDictionary.Source can be set only once.");
            }

            _source = value;
            var absolute = value.IsAbsoluteUri ? value : new Uri("pack://application:,,," + value.OriginalString, UriKind.Absolute);
            var loaded = t_loaded ??= new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);
            if (!loaded.TryGetValue(absolute.AbsoluteUri, out var shared))
            {
                shared = new ResourceDictionary { Source = absolute };
                loaded[absolute.AbsoluteUri] = shared;
            }

            MergedDictionaries.Add(shared);
        }
    }
}
