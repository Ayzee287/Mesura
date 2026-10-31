using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Mesura.App;

/// <summary>
/// Lets a card with a chevron open and close by a click on the card itself, not only on the small chevron (0.4.1). Set on the chevron's style, so
/// every page that has one gets it: the toggle finds the card it belongs to (the highest ancestor that shares its data context) and passes the card's
/// clicks to itself. Clicks on a button, a switch, a text box, a list, a link or a scroll bar stay theirs, and so does everything below the card's
/// header row, so text in an open card can still be selected.
/// </summary>
public static class ExpandOnClick
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ExpandOnClick), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleButton toggle && e.NewValue is true)
        {
            toggle.Loaded += (_, _) => Attach(toggle);
        }
    }

    private static void Attach(ToggleButton toggle)
    {
        var card = CardOf(toggle);
        if (card is null || card == toggle)
        {
            return;
        }

        NameByCard(toggle, card);

        void Handler(object sender, MouseButtonEventArgs args)
        {
            if (args.Handled || !toggle.IsVisible || !toggle.IsEnabled || Interactive(args.OriginalSource as DependencyObject, card))
            {
                return;
            }

            // Only the header row: from the card's top to just below the chevron.
            var position = args.GetPosition(card);
            var chevron = toggle.TransformToAncestor(card).TransformBounds(new Rect(0, 0, toggle.ActualWidth, toggle.ActualHeight));
            if (position.Y > chevron.Bottom + 14)
            {
                return;
            }

            toggle.IsChecked = toggle.IsChecked != true;
            args.Handled = true;
        }

        card.MouseLeftButtonUp -= Handler;
        card.MouseLeftButtonUp += Handler;
    }

    /// <summary>
    /// A row of identical chevrons all named "Details" tells a screen reader nothing: name each one after its card ("Windows settings, Details"), from the first text the card shows.
    /// Only when the toggle still carries the generic name.
    /// </summary>
    private static void NameByCard(ToggleButton toggle, DependencyObject card)
    {
        var generic = Loc.Get("Common.Details");
        if (System.Windows.Automation.AutomationProperties.GetName(toggle) != generic)
        {
            return;
        }

        var title = FirstText(card, toggle);
        if (title.Length > 0)
        {
            System.Windows.Automation.AutomationProperties.SetName(toggle, $"{title}, {generic}");
        }
    }

    private static string FirstText(DependencyObject node, DependencyObject skip)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (ReferenceEquals(child, skip))
            {
                continue;
            }

            if (child is TextBlock { Text.Length: > 1, IsVisible: true } text && !string.IsNullOrWhiteSpace(text.Text)) // one character is an icon glyph
            {
                return text.Text.Trim();
            }

            var inner = FirstText(child, skip);
            if (inner.Length > 0)
            {
                return inner;
            }
        }

        return "";
    }

    /// <summary>The card: the highest ancestor whose data context is the toggle's own (so a template's root, or the surface that carries the item).</summary>
    private static FrameworkElement? CardOf(FrameworkElement toggle)
    {
        var context = toggle.DataContext;
        FrameworkElement? card = null;
        for (var parent = VisualTreeHelper.GetParent(toggle); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement element)
            {
                continue;
            }

            if (!ReferenceEquals(element.DataContext, context) || element is ScrollViewer or Window or ScrollContentPresenter)
            {
                break;
            }

            // A list item's container (ListBoxItem and the like) is the card's edge: clicking it selects, which is not this behaviour's job.
            if (element is ListBoxItem or ListViewItem or ItemsControl or Panel { Children.Count: > 8 })
            {
                break;
            }

            card = element;
        }

        return card;
    }

    private static bool Interactive(DependencyObject? source, DependencyObject card)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, card); node = Parent(node))
        {
            if (node is ButtonBase or TextBoxBase or Selector or ScrollBar or Thumb or ComboBox or Hyperlink or Slider or PasswordBox or Expander or ItemsControl or Popup)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
