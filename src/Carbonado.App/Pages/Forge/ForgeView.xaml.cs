using System.Windows.Controls;
using System.Windows.Input;
using Carbonado.UI.Controls;

namespace Carbonado.App.Pages.Forge;

/// <summary>The Forge page: drop zone, recipe toolbar, queue and live savings.</summary>
public partial class ForgeView : UserControl
{
    public ForgeView() => InitializeComponent();

    private ForgeViewModel? ViewModel => DataContext as ForgeViewModel;

    private async void OnFilesDropped(object? sender, FilesDroppedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddPathsAsync(e.Paths).ConfigureAwait(true);
        }
    }

    private void OnQueueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Queue.SelectedItem is QueueItemViewModel item && ViewModel is { } vm)
        {
            if (item.Result is not null)
            {
                vm.CompareCommand.Execute(item);
            }
            else
            {
                vm.OpenOutputCommand.Execute(item);
            }
        }
    }

    private void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel is { } vm)
        {
            foreach (var item in Queue.SelectedItems.Cast<QueueItemViewModel>().ToList())
            {
                vm.RemoveCommand.Execute(item);
            }

            e.Handled = true;
        }
    }
}
