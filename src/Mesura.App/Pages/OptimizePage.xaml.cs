using System.Windows;
using System.Windows.Controls;

namespace Mesura.App.Pages;

/// <summary>Optimize: the home page (<see cref="OptimizeViewModel"/>).</summary>
public partial class OptimizePage : UserControl, IDockPage
{
    /// <summary>The page's summary and primary action, shown in the command dock while the page is open.</summary>
    public DataTemplate? DockTemplate => (DataTemplate?)Resources["DockActions"];

    public OptimizePage() => InitializeComponent();
}
