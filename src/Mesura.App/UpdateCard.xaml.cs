using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App;

/// <summary>The sidebar's update notice; clicking it opens the update dialog (release notes and the choices) when an update is waiting.</summary>
public partial class UpdateCard : UserControl
{
    public UpdateCard() => InitializeComponent();

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is UpdateViewModel { HasUpdate: true } update)
        {
            update.OpenDialog();
        }
    }
}
