using System.Windows.Controls;

namespace Mesura.App.Pages;

public partial class SystemInfoPage : UserControl
{
    public SystemInfoPage() => InitializeComponent();

    /// <summary>One column in a narrow window, two in a medium one, three when the page has the room: cards keep a readable width.</summary>
    private void OnColumnsResized(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (DataContext is SystemInfoViewModel vm && e.WidthChanged)
        {
            var width = e.NewSize.Width;
            vm.SetColumns(width < 620 ? 1 : width < 1060 ? 2 : 3);
        }
    }
}
