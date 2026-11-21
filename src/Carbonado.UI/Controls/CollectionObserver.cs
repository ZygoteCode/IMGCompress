using System.Collections.Specialized;
using System.Windows;

namespace Carbonado.UI.Controls;

/// <summary>
/// Watches the <see cref="INotifyCollectionChanged"/> data of a control only while that control is loaded.
/// A long-lived view-model collection therefore never keeps a discarded control (and its page) alive: the
/// handlers are removed on Unloaded and re-attached on Loaded. Data properties call <see cref="Refresh"/>.
/// </summary>
internal sealed class CollectionObserver
{
    private readonly FrameworkElement _owner;
    private readonly Func<IEnumerable<object?>> _sources;
    private readonly NotifyCollectionChangedEventHandler _handler;
    private readonly List<INotifyCollectionChanged> _attached = [];

    public CollectionObserver(FrameworkElement owner, Func<IEnumerable<object?>> sources, Action changed)
    {
        _owner = owner;
        _sources = sources;
        _handler = (_, _) => changed();
        owner.Loaded += (_, _) => Refresh();
        owner.Unloaded += (_, _) => Detach();
    }

    /// <summary>Number of collections currently observed (diagnostics and tests).</summary>
    public int Count => _attached.Count;

    /// <summary>Re-reads the sources: attaches to the current ones while loaded, otherwise stays detached.</summary>
    public void Refresh()
    {
        Detach();
        if (!_owner.IsLoaded)
        {
            return;
        }

        foreach (var source in _sources())
        {
            if (source is INotifyCollectionChanged collection && !_attached.Contains(collection))
            {
                collection.CollectionChanged += _handler;
                _attached.Add(collection);
            }
        }
    }

    private void Detach()
    {
        foreach (var collection in _attached)
        {
            collection.CollectionChanged -= _handler;
        }

        _attached.Clear();
    }
}
