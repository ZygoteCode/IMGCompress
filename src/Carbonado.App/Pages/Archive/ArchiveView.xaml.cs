using System.Windows.Controls;
using System.Windows.Input;

namespace Carbonado.App.Pages.Archive;

/// <summary>Archive page: lifetime stats, 14-day savings, format distribution, recent jobs.</summary>
public partial class ArchiveView : UserControl
{
    public ArchiveView() => InitializeComponent();

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is HistoryRow row && DataContext is ArchiveViewModel vm)
        {
            vm.OpenCommand.Execute(row);
        }
    }
}
