using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App.Pages;

/// <summary>All settings (<see cref="AllSettingsViewModel"/>).</summary>
public partial class AllSettingsPage : UserControl
{
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

    private void OnCollectionChosen(object sender, RoutedEventArgs e) => CollectionsButton.IsChecked = false;

    private void OnCollectionsClosed(object? sender, EventArgs e) => CollectionsButton.IsChecked = false;
}
