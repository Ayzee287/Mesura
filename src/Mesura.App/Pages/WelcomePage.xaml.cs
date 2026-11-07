using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mesura.App.Pages;

/// <summary>The page Mesura opens on (<see cref="WelcomeViewModel"/>): a greeting and the way in, not a dashboard.</summary>
public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
        BuildTitle();
        Loc.Instance.LanguageChanged += BuildTitle;
    }

    /// <summary>
    /// "Welcome to Mesura" with the mark itself as the M of the word: the logo stands in the line as a letter, on the baseline, as tall as the capitals. The mark's picture has room round
    /// the letter for its glow (the letter fills about 56 % of the width and 45 % of the height), so the image is drawn larger than the letter and pulled back with negative margins.
    /// A title without the word is plain text. The title is read out in full (the image carries no letter of its own).
    /// </summary>
    private void BuildTitle()
    {
        var text = Loc.Get("Welcome.Title");
        Title.Inlines.Clear();
        AutomationProperties.SetName(Title, text);
        var at = text.IndexOf(Branding.Name, StringComparison.Ordinal);
        if (at < 0)
        {
            Title.Inlines.Add(new Run(text));
            return;
        }

        Title.Inlines.Add(new Run(text[..at]));
        var side = Title.FontSize * 1.6;   // the letter inside is about 0.72 of the font size tall: the capital height of this typeface
        var image = new Image
        {
            Source = Branding.Mark,
            Width = side,
            Height = side,
            Margin = new Thickness(-side * 0.215, -side * 0.27, -side * 0.12, -side * 0.27),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Title.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Baseline });
        Title.Inlines.Add(new Run(text[(at + 1)..]));
    }
}
