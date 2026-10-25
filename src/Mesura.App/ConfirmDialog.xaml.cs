using System.Windows;
using System.Windows.Input;

namespace Mesura.App;

/// <summary>
/// Mesura's confirmation before a change: a title, one line, the list of what will change and a note, over the Mesura
/// window in the app's own style. Enter confirms, Escape cancels.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, IReadOnlyList<string> items, string note, string confirm, string? cancel)
    {
        InitializeComponent();
        Title = Branding.Name;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.ItemsSource = items;
        ItemsHost.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = note;
        NoteBox.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = confirm;
        CancelButton.Content = cancel ?? Loc.Get("Common.Cancel");
        CancelButton.Visibility = cancel == "" ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Asks to confirm; true when the user confirmed.</summary>
    public static bool Ask(string title, string message, IReadOnlyList<string>? items = null, string note = "", string? confirm = null)
    {
        var dialog = new ConfirmDialog(title, message, items ?? [], note, confirm ?? Loc.Get("Common.Continue"), null);
        return dialog.ShowOver() == true;
    }

    /// <summary>Tells the user something, with one button.</summary>
    public static void Tell(string title, string message, string note = "")
    {
        new ConfirmDialog(title, message, [], note, Loc.Get("Common.Ok"), "").ShowOver();
    }

    private bool? ShowOver()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != this)
            ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != this)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }
}
