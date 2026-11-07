using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

/// <summary>
/// 0.7: asking winget whether an app is installed (a read) never needs the launch as the signed-in user that installing a per-user app needs. That launch depends on the shell's
/// token and, for some PCs, on the Secondary Logon service; when it failed, every per-user app (Discord, Spotify, Vencord) read "Can't check" while an elevated Mesura was running.
/// </summary>
public sealed class WingetQueryContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_question_by_the_same_user_runs_directly_elevated_or_not(bool elevated)
    {
        var (launch, reason) = WingetContext.Decide(userKnown: true, sameUser: true, elevated, WingetContext.Query, signedInUserReachable: false);

        Assert.Equal(WingetLaunch.Direct, launch);
        Assert.Equal("", reason);
    }

    [Fact]
    public void The_same_elevated_user_installing_a_per_user_app_still_needs_the_signed_in_user_launch()
    {
        // Unchanged: an install must not run elevated for a per-user app, so without the launch it is refused with the reason.
        var (launch, reason) = WingetContext.Decide(userKnown: true, sameUser: true, elevated: true, AppRunContext.SignedInUser, signedInUserReachable: false);

        Assert.Equal(WingetLaunch.Refused, launch);
        Assert.NotEqual("", reason);
    }

    [Fact]
    public void Another_account_elevated_is_still_asked_as_the_signed_in_user()
    {
        // A standard user elevated with an administrator's password: the elevated profile is the administrator's, so the question goes to the signed-in user's.
        Assert.Equal(WingetLaunch.AsSignedInUser, WingetContext.Decide(true, sameUser: false, elevated: true, WingetContext.Query, signedInUserReachable: true).Launch);
        Assert.Equal(WingetLaunch.Refused, WingetContext.Decide(true, sameUser: false, elevated: true, WingetContext.Query, signedInUserReachable: false).Launch);
    }

    [Fact]
    public void The_query_context_is_any()
    {
        Assert.Equal(AppRunContext.Any, WingetContext.Query);
    }
}
