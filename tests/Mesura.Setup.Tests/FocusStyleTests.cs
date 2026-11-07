using System.Xml.Linq;

namespace Mesura.Setup.Tests;

/// <summary>
/// A mouse click leaves keyboard focus on a button, so a template that lights a button from <c>IsKeyboardFocused</c> keeps a halo or a rim after the click (the "ghost contour",
/// 0.7). The keyboard's own ring is the <c>FocusVisualStyle</c> (it appears only for keyboard navigation). Text inputs may use <c>IsKeyboardFocused</c>: a focused field is meant to look focused.
/// </summary>
public sealed class FocusStyleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string AppFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Mesura.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src\\Mesura.App was not found above the test folder.");
    }

    private static readonly string[] TextInputs = ["TextBox", "PasswordBox", "ComboBox", "RichTextBox"];

    [Fact]
    public void No_button_template_is_lit_by_keyboard_focus_which_a_mouse_click_also_sets()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppFolder(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var document = XDocument.Load(file);
            foreach (var template in document.Descendants(Wpf + "ControlTemplate"))
            {
                var target = (string?)template.Attribute("TargetType") ?? "";
                if (TextInputs.Any(t => target.EndsWith(t, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var trigger in template.Descendants(Wpf + "Trigger").Where(t => (string?)t.Attribute("Property") == "IsKeyboardFocused"))
                {
                    offenders.Add($"{Path.GetFileName(file)}: a {target} template has an IsKeyboardFocused trigger");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_logo_button_draws_the_keyboard_ring_like_every_other_button()
    {
        var theme = XDocument.Load(Path.Combine(AppFolder(), "Theme.xaml"));
        var logo = theme.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "LogoButton");
        Assert.Contains(logo.Elements(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "FocusVisualStyle" && ((string?)s.Attribute("Value"))?.Contains("FocusRing", StringComparison.Ordinal) == true);
    }
}
