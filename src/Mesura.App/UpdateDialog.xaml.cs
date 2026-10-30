using System.Windows;
using System.Windows.Input;

namespace Mesura.App;

/// <summary>The update dialog: release notes and the choices (install and restart, remind tomorrow, skip this version).</summary>
public partial class UpdateDialog : Window
{
    public UpdateDialog(UpdateViewModel update)
    {
        InitializeComponent();
        Title = Branding.Name;
        DataContext = update;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }
}
