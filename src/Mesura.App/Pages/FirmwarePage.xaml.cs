using System.Windows;
using System.Windows.Controls;

namespace Mesura.App.Pages;

/// <summary>BIOS &amp; Firmware (<see cref="FirmwareViewModel"/>).</summary>
public partial class FirmwarePage : UserControl, IDockPage
{
    public FirmwarePage() => InitializeComponent();

    /// <summary>The page's summary and actions, shown in the command dock while the page is open.</summary>
    public DataTemplate? DockTemplate => (DataTemplate?)Resources["DockActions"];
}
