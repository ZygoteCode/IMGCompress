using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Carbonado.App.Services;

namespace Carbonado.App.Shell;

/// <summary>Confirmation content shown inside the <see cref="UI.Controls.ModalHost"/> card.</summary>
public partial class ConfirmDialog : UserControl
{
    public ConfirmDialog(string title, string message, string primary, string? secondary, bool danger)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        PrimaryButton.Style = (Style)FindResource(danger ? "Button.Danger" : "Button.Primary");
        Glyph.Icon = (Geometry)FindResource(danger ? "Icon.Warning" : "Icon.Info");
        Glyph.Foreground = (Brush)FindResource(danger ? "B.Warning" : "B.Accent");
        if (secondary is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondary;
        }

        Loaded += (_, _) => PrimaryButton.Focus();
    }

    /// <summary>Raised once with the user's choice.</summary>
    public event EventHandler<ConfirmResult>? Answered;

    private void OnPrimary(object sender, RoutedEventArgs e) => Answered?.Invoke(this, ConfirmResult.Primary);

    private void OnSecondary(object sender, RoutedEventArgs e) => Answered?.Invoke(this, ConfirmResult.Secondary);
}
