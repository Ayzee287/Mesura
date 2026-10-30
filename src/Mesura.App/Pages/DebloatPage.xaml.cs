using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App.Pages;

/// <summary>Debloat (<see cref="DebloatViewModel"/>).</summary>
public partial class DebloatPage : UserControl, IDockPage
{
    /// <summary>The page's summary and primary action, shown in the command dock while the page is open.</summary>
    public DataTemplate? DockTemplate => (DataTemplate?)Resources["DockActions"];

    public DebloatPage() => InitializeComponent();

    /// <summary>Space on a selected card ticks or unticks it, so the list works without a mouse.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && List.SelectedItem is SettingRow { CanSelect: true } row)
        {
            row.Selected = !row.Selected;
            e.Handled = true;
        }
    }
}
