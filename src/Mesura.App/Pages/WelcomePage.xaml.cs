using System.Windows.Controls;

namespace Mesura.App.Pages;

/// <summary>The page Mesura opens on (<see cref="WelcomeViewModel"/>): a greeting and the way in, not a dashboard.</summary>
public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
        Mark.Source = Branding.Mark;
    }
}
