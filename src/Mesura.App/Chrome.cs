using System.Windows;

namespace Mesura.App;

/// <summary>
/// State the whole shell shares with the controls inside it. <see cref="CompactProperty"/> is inherited: the shell sets it on the
/// sidebar when the window is narrow, and every navigation item below reads it (its label hides, its tooltip stays).
/// </summary>
public static class Chrome
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(Chrome), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);

    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);
}
