using System.Windows;
using System.Windows.Controls;

namespace Mesura.App;

/// <summary>
/// The opening of a page: an eyebrow, the title, a subtitle and the page's main actions. Every page uses it with the same
/// margin and title size, so the pages read as one product and the content doesn't jump between them.
/// </summary>
public partial class PageHero : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(PageHero), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHero), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHero), new PropertyMetadata(""));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHero));
    public static readonly DependencyProperty HeroMarginProperty = DependencyProperty.Register(nameof(HeroMargin), typeof(Thickness), typeof(PageHero), new PropertyMetadata(new Thickness(36, 28, 36, 12)));
    public static readonly DependencyProperty TitleSizeProperty = DependencyProperty.Register(nameof(TitleSize), typeof(double), typeof(PageHero), new PropertyMetadata(28.0));

    public PageHero() => InitializeComponent();

    public string Eyebrow { get => (string)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>The page's main actions, shown on the right.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public Thickness HeroMargin { get => (Thickness)GetValue(HeroMarginProperty); set => SetValue(HeroMarginProperty, value); }

    public double TitleSize { get => (double)GetValue(TitleSizeProperty); set => SetValue(TitleSizeProperty, value); }
}
