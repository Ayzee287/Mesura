using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mesura.App.Pages;

/// <summary>Apps (<see cref="AppsViewModel"/>).</summary>
public partial class AppsPage : UserControl, IDockPage
{
    /// <summary>The page's summary and primary action, shown in the command dock while the page is open.</summary>
    public DataTemplate? DockTemplate => (DataTemplate?)Resources["DockActions"];

    public AppsPage() => InitializeComponent();

    /// <summary>A click anywhere on an app's card ticks or unticks it (the tick box itself handles its own click).</summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) is not null)
        {
            return;
        }

        // A click on a customisation's row ticks the customisation, not the app; its title is made of text runs, which
        // are content elements, so the row is found by walking up through both trees.
        if (e.OriginalSource is DependencyObject origin && ModOf(origin) is { } mod)
        {
            if (mod.CanSelect)
            {
                mod.Selected = !mod.Selected;
            }

            return;
        }

        if (sender is FrameworkElement { DataContext: AppItem { CanSelect: true } item })
        {
            item.Selected = !item.Selected;
        }
    }

    private static AppMod? ModOf(DependencyObject child)
    {
        for (var current = child; current is not null;
             current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            switch (current)
            {
                case FrameworkElement { DataContext: AppMod mod }:
                    return mod;
                case FrameworkContentElement { DataContext: AppMod mod }:
                    return mod;
                case FrameworkElement { DataContext: AppItem }:
                    return null; // reached the app's own part of the card
            }
        }

        return null;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null;
             current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
