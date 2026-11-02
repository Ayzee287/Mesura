using System.Text.RegularExpressions;

namespace Mesura.Setup.Tests;

/// <summary>
/// The installer's authoring (installer\Package.wxs) read as text, for what must not regress in the scope choice (0.5): the wizard asks who the
/// copy is for, "only for me" is the default, and an all-users copy's folder is not derived from a folder property that is wrong while the wizard
/// still runs per-user. Built packages were checked separately (docs/installing.md); these pin the source.
/// </summary>
public sealed partial class InstallerPackageTests
{
    private static string Installer(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "installer", file);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new InvalidOperationException("installer\\" + file + " wasn't found above the test folder.");
    }

    [Fact]
    public void The_wizard_goes_welcome_scope_install_with_all_users_as_the_recommended_default()
    {
        var wxs = Installer("Package.wxs");

        Assert.Contains("<Property Id=\"MESURA_SCOPE\" Value=\"machine\" />", wxs, StringComparison.Ordinal);
        Assert.Contains("Dialog=\"WelcomeDlg\" Control=\"Next\" Event=\"NewDialog\" Value=\"MesuraScopeDlg\"", wxs, StringComparison.Ordinal);
        Assert.Contains("Dialog=\"VerifyReadyDlg\" Control=\"Back\" Event=\"NewDialog\" Value=\"MesuraScopeDlg\"", wxs, StringComparison.Ordinal);
        Assert.Contains("Scope=\"perUserOrMachine\"", wxs, StringComparison.Ordinal);
        // A silent install is decided by its command line (the updater passes ALLUSERS itself); the page is only for the wizard.
        Assert.DoesNotContain("SetProperty Id=\"MESURA_SCOPE\"", wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void An_all_users_folder_is_built_from_the_environment_because_the_wizard_runs_per_user()
    {
        var wxs = Installer("Package.wxs");
        var next = Regex.Match(wxs, "<Dialog Id=\"MesuraScopeDlg\".*?</Dialog>", RegexOptions.Singleline).Value;

        Assert.NotEmpty(next);
        // [ProgramFiles64Folder] is %LOCALAPPDATA%\Programs in a per-user session, so it would put an "all users" copy in the profile.
        Assert.DoesNotContain("[ProgramFiles64Folder]", next, StringComparison.Ordinal);
        Assert.Contains(@"[%ProgramW6432]\Mesura", next, StringComparison.Ordinal);
        Assert.Contains(@"[%LOCALAPPDATA]\Programs\Mesura", next, StringComparison.Ordinal);
        // Both ALLUSERS values and the per-user flag are written, so Windows asks for administrator approval only for the all-users choice.
        Assert.Contains("Property=\"ALLUSERS\" Value=\"1\" Condition=\"MESURA_SCOPE = &quot;machine&quot;\"", next, StringComparison.Ordinal);
        Assert.Contains("Property=\"ALLUSERS\" Value=\"2\" Condition=\"MESURA_SCOPE = &quot;user&quot;\"", next, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scope_page_warns_when_a_copy_of_the_other_kind_is_registered()
    {
        var wxs = Installer("Package.wxs");

        // A per-user and an all-users install are two products to Windows Installer: the other choice adds a copy beside the old one, so the page says so.
        Assert.Contains("<SetProperty Id=\"SCOPENOTEUSER\" Value=\"!(loc.ScopeOtherUserCopy)\" After=\"AppSearch\" Sequence=\"ui\" Condition=\"PREVINSTALLDIR AND NOT PREVINSTALLDIRMACHINE\"", wxs, StringComparison.Ordinal);
        Assert.Contains("<SetProperty Id=\"SCOPENOTEMACHINE\" Value=\"!(loc.ScopeOtherMachineCopy)\" After=\"AppSearch\" Sequence=\"ui\" Condition=\"PREVINSTALLDIRMACHINE AND NOT PREVINSTALLDIR\"", wxs, StringComparison.Ordinal);
        Assert.Contains("Text=\"[SCOPENOTEUSER][SCOPENOTEMACHINE]\"", wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_the_package_that_is_already_installed_shows_a_page_saying_no_second_copy_can_be_made()
    {
        var wxs = Installer("Package.wxs");

        // Windows Installer keeps one copy of a product code: the wizard goes to Change / Repair / Remove and never offers the other scope (measured in Sandbox), so the owner's warning sits
        // between the maintenance welcome and that choice, and names the scope of the installed copy.
        Assert.Contains("<SetProperty Id=\"SAMECOPYUSER\" Value=\"!(loc.SameCopyUser)\" After=\"AppSearch\" Sequence=\"ui\" Condition=\"Installed AND NOT PATCH AND PREVINSTALLDIR AND NOT PREVINSTALLDIRMACHINE\"", wxs, StringComparison.Ordinal);
        Assert.Contains("<SetProperty Id=\"SAMECOPYMACHINE\" Value=\"!(loc.SameCopyMachine)\" After=\"AppSearch\" Sequence=\"ui\" Condition=\"Installed AND NOT PATCH AND PREVINSTALLDIRMACHINE\"", wxs, StringComparison.Ordinal);
        Assert.Contains("Dialog=\"MaintenanceWelcomeDlg\" Control=\"Next\" Event=\"NewDialog\" Value=\"MesuraSameCopyDlg\"", wxs, StringComparison.Ordinal);
        Assert.Contains("Dialog=\"MaintenanceTypeDlg\" Control=\"Back\" Event=\"NewDialog\" Value=\"MesuraSameCopyDlg\"", wxs, StringComparison.Ordinal);
        var page = Regex.Match(wxs, "<Dialog Id=\"MesuraSameCopyDlg\".*?</Dialog>", RegexOptions.Singleline).Value;
        Assert.Contains("[SAMECOPYUSER][SAMECOPYMACHINE]", page, StringComparison.Ordinal);
        Assert.Contains("[SAMECOPYHOWUSER][SAMECOPYHOWMACHINE]", page, StringComparison.Ordinal);
        Assert.Contains("Value=\"MaintenanceTypeDlg\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_string_the_package_uses_exists_in_both_wizard_languages()
    {
        var used = LocString().Matches(Installer("Package.wxs")).Select(m => m.Groups[1].Value).Where(id => !id.StartsWith("WixUI", StringComparison.Ordinal)).ToHashSet();
        var english = Ids(Installer("Strings.en-us.wxl"));
        var russian = Ids(Installer("Strings.ru-ru.wxl"));

        Assert.Equal(english.Order(), russian.Order());
        Assert.All(used, id => Assert.Contains(id, english));
        Assert.Contains("ScopeUser", used);
    }

    private static HashSet<string> Ids(string wxl) => [.. StringId().Matches(wxl).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"!\(loc\.([A-Za-z0-9_]+)\)")]
    private static partial Regex LocString();

    [GeneratedRegex("<String Id=\"([A-Za-z0-9_]+)\"")]
    private static partial Regex StringId();
}
