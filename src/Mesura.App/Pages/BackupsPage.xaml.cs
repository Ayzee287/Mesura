using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mesura.App.Pages;

/// <summary>Backups (<see cref="BackupsViewModel"/>): the PC setup and app settings tabs, one Open door and one New menu.</summary>
public partial class BackupsPage : UserControl
{
    public BackupsPage() => InitializeComponent();

    /// <summary>
    /// The tabs are a group: an arrow key moves to the other tab and opens it (as tabs do), so a keyboard user does not have to press Space as well. Tab and Shift+Tab leave the group
    /// as for any radio group.
    /// </summary>
    private void OnTabFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is RadioButton tab && e.OldFocus is RadioButton other && other != tab && other.GroupName == tab.GroupName)
        {
            tab.IsChecked = true;
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        // The menu hangs from the button's right edge, so it stays inside the window when the button is at the right of the row.
        if (NewPopup.Child is FrameworkElement menu)
        {
            NewPopup.HorizontalOffset = NewButton.ActualWidth - menu.Width - 10;
        }

        NewPopup.IsOpen = true;
        // A popup is its own window: the keyboard stays on the button unless it is moved in, so the first entry takes the focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NewSetupItem.Focus());
    }

    private bool _returnFocus;

    private void OnNewChosen(object sender, RoutedEventArgs e)
    {
        _returnFocus = true;
        NewPopup.IsOpen = false;
    }

    /// <summary>Esc closes the menu; the focus goes back to the button that opened it.</summary>
    private void OnNewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _returnFocus = true;
            NewPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>After Esc or a choice the keyboard goes back to the button; after a click elsewhere it stays where the click put it.</summary>
    private void OnNewClosed(object? sender, EventArgs e)
    {
        if (_returnFocus)
        {
            _returnFocus = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NewButton.Focus());
        }
    }
}
