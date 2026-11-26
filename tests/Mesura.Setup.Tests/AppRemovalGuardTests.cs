using Mesura.Core.Windows;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Setup.Tests;

/// <summary>
/// The last check before a built-in app is removed or put back (0.9): <see cref="WindowsWriter"/> refuses unless this process runs as the signed-in user,
/// because Windows does both for the account that asks. Every case here is refused before any process could start, so nothing is removed or registered on
/// this PC; the allowed case would start PowerShell and is therefore not run here (the engine tests cover it with a stand-in Windows). Safe by construction
/// as well: the package names a well-formed package that exists on no PC, so even a broken guard could not remove or register anything real.
/// </summary>
public sealed class AppRemovalGuardTests
{
    private const string News = "Mesura.GuardTestNoSuchPackage_1.0.0.0_x64__0000000000000";
    private const string NewsFamily = "Mesura.GuardTestNoSuchPackage_0000000000000";

    [Fact]
    public void A_context_that_says_the_process_is_another_account_refuses_removal_and_registration()
    {
        var writer = new WindowsWriter(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: false), processIsUser: _ => true);

        var remove = Assert.Throws<InvalidOperationException>(() => writer.RemoveAppx(News));
        Assert.Contains("another account than PC-owner", remove.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => writer.RestoreAppx(NewsFamily));
    }

    [Fact]
    public void The_process_identity_is_checked_again_even_when_the_context_says_it_is_the_user()
    {
        var asked = new List<string?>();
        var writer = new WindowsWriter(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: true), processIsUser: user =>
        {
            asked.Add(user.Sid);
            return false;   // the process now runs as someone else
        });

        Assert.Throws<InvalidOperationException>(() => writer.RemoveAppx(News));
        Assert.Throws<InvalidOperationException>(() => writer.RestoreAppx(NewsFamily));
        Assert.Equal(["S-1-5-21-1-1-1-1001", "S-1-5-21-1-1-1-1001"], asked);
    }

    [Fact]
    public void A_name_that_is_not_a_package_is_refused_before_the_account_is_even_asked()
    {
        var asked = false;
        var writer = new WindowsWriter(new UserContext("S-1-5-21-1-1-1-1001", "PC-owner", "", ProcessIsUser: true), processIsUser: _ => asked = true);

        Assert.Throws<ArgumentException>(() => writer.RemoveAppx("not a package; rm -rf"));
        Assert.Throws<ArgumentException>(() => writer.RestoreAppx("not a family"));
        Assert.False(asked);
    }
}
