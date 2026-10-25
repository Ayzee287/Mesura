using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App.Pages;

/// <summary>
/// Activation (<see cref="ActivationViewModel"/>). The product key field is a masked password box, which WPF doesn't
/// bind: its text goes to the view model on Install and the box is cleared at once, so the key isn't kept on screen or
/// in a bound property.
/// </summary>
public partial class ActivationPage : UserControl
{
    public ActivationPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => KeyBox.Clear();
    }

    private async void OnInstallKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ActivationViewModel model)
        {
            return;
        }

        var typed = KeyBox.Password;
        KeyBox.Clear();
        try
        {
            await model.InstallKeyAsync(typed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ConfirmDialog.Tell(Loc.Get("Common.Unexpected"), ex.Message);
        }
    }

    private void OnCancelKey(object sender, RoutedEventArgs e)
    {
        KeyBox.Clear();
        (DataContext as ActivationViewModel)?.CancelKey.Execute(null);
    }

    private void OnKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnInstallKey(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCancelKey(sender, e);
            e.Handled = true;
        }
    }
}
