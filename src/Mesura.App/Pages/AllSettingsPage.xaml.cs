using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App.Pages;

/// <summary>All settings (<see cref="AllSettingsViewModel"/>).</summary>
public partial class AllSettingsPage : UserControl, IDockPage
{
    /// <summary>The page's summary and primary action, shown in the command dock while the page is open.</summary>
    public DataTemplate? DockTemplate => (DataTemplate?)Resources["DockActions"];

    public AllSettingsPage() => InitializeComponent();

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Space on a selected entry ticks or unticks it, so the list works without a mouse.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && List.SelectedItem is SettingRow { CanSelect: true } row)
        {
            row.Selected = !row.Selected;
            e.Handled = true;
        }
    }

    /// <summary>Closing the drawer clears the selection (a ticked setting stays ticked).</summary>
    private void OnCloseDetails(object sender, RoutedEventArgs e)
    {
        if (DataContext is AllSettingsViewModel vm)
        {
            vm.SelectedRow = null;
        }
    }

    private void OnCollectionChosen(object sender, RoutedEventArgs e) => CollectionsButton.IsChecked = false;

    private void OnCollectionsClosed(object? sender, EventArgs e) => CollectionsButton.IsChecked = false;
}
