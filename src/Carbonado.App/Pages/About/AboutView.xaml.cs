using System.Windows.Controls;
using Carbonado.UI.Brand;

namespace Carbonado.App.Pages.About;

/// <summary>About page: brand mark, version, startup timing, story and licenses.</summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        Wordmark.Data = WordmarkGeometry.Compose();
    }
}
