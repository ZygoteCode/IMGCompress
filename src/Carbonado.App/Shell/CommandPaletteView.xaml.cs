using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Carbonado.App.Shell;

/// <summary>Ctrl+K command palette: type to filter, arrows to move, Enter to run, Esc to close (handled by the window).</summary>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView() => InitializeComponent();

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    /// <summary>Moves keyboard focus into the search box once the palette is visible.</summary>
    public void FocusSearch() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Search.Focus();
            Keyboard.Focus(Search);
            Search.SelectAll();
        });

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                ViewModel?.MoveSelection(1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel?.MoveSelection(-1);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                ViewModel?.ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is PaletteCommand command)
        {
            ViewModel?.Run(command);
        }
    }
}
