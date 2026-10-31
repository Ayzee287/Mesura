using System.Windows;
using System.Windows.Input;

namespace Mesura.App;

/// <summary>
/// Mesura's confirmation before a change: a title, one line, the list of what will change and a note, over the Mesura
/// window in the app's own style. Enter confirms, Escape cancels.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, IReadOnlyList<string> items, string note, string confirm, string? cancel, string? remember = null)
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
        RememberBox.Content = remember;
        RememberBox.Visibility = remember is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (remember is { Length: > 0 })
        {
            System.Windows.Automation.AutomationProperties.SetName(RememberBox, remember);
        }
    }

    /// <summary>
    /// A choice between two actions with an optional "remember my choice" box: <c>true</c> is the primary (confirm) button, <c>false</c> the other, <c>null</c>
    /// closing the dialog (Escape) without choosing. Used at start, before the main window exists.
    /// </summary>
    public static bool? Choose(string title, string message, string primary, string secondary, string remember, out bool remembered, string note = "")
    {
        var dialog = new ConfirmDialog(title, message, [], note, primary, secondary, remember);
        var result = dialog.ShowOver();
        remembered = dialog.RememberBox.IsChecked == true && result is not null;
        return result;
    }

    /// <summary>
    /// Asks to confirm and to choose one of several ways to do it, once for the whole action. Returns the chosen key, or <c>null</c> when the user
    /// cancelled.
    /// </summary>
    public static string? AskChoice(string title, IReadOnlyList<string> items, IReadOnlyList<DialogChoice> choices, string note, string confirm)
    {
        var dialog = new ConfirmDialog(title, "", items, note, confirm, null);
        dialog.ChoiceList.ItemsSource = choices;
        dialog.ChoiceList.Visibility = Visibility.Visible;
        dialog.ItemsHost.MaxHeight = 120;
        return dialog.ShowOver() == true ? choices.FirstOrDefault(c => c.IsSelected && c.Enabled)?.Key : null;
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

/// <summary>One way of doing an action, offered as a card in <see cref="ConfirmDialog.AskChoice"/>; disabled when it can't be used (with the reason as its hint).</summary>
public sealed class DialogChoice(string key, string title, string hint, bool enabled, bool selected) : Observable
{
    private bool _selected = selected;

    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Hint { get; } = hint;

    public bool Enabled { get; } = enabled;

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}
