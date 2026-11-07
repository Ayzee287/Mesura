using Mesura.Core.Support;

namespace Mesura.Core.Tests.Support;

/// <summary>
/// 0.7 (D049): Mesura asks Windows for administrator rights when it starts, by default and without a dialog first. These pin the decision table:
/// who is asked, who is not, and the guards that stop a loop of prompts or restarts.
/// </summary>
public sealed class StartupPolicyTests
{
    private const bool Protected = true;
    private const bool Shared = false;

    private static StartupAction Decide(string? saved = null, bool legacy = true, bool folderProtected = Protected, bool elevated = false, bool relaunched = false, bool noElevate = false, bool tray = false) =>
        StartupPolicy.Decide(saved, legacy, folderProtected, elevated, relaunched, noElevate, tray);

    [Fact]
    public void A_plain_start_with_nothing_ever_chosen_asks_Windows_straight_away()
    {
        Assert.Equal(StartupAction.Elevate, Decide());
    }

    [Fact]
    public void Every_user_who_never_touched_the_older_switch_gets_the_new_default()
    {
        // A 0.6 settings file has no StartupMode and ElevateAtStart true: it used to mean "show the choice", now it means "ask Windows".
        Assert.Equal("admin", StartupPolicy.EffectiveMode(null, true));
        Assert.Equal(StartupAction.Elevate, Decide(saved: null, legacy: true));
    }

    [Fact]
    public void A_choice_made_on_purpose_is_kept()
    {
        Assert.Equal(StartupAction.Continue, Decide(saved: "normal"));
        Assert.Equal(StartupAction.Ask, Decide(saved: "ask"));
        Assert.Equal(StartupAction.Elevate, Decide(saved: "admin"));
        // The older switch turned off on purpose, with no mode saved, is still "never".
        Assert.Equal("normal", StartupPolicy.EffectiveMode(null, false));
        Assert.Equal(StartupAction.Continue, Decide(saved: null, legacy: false));
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("")]
    [InlineData("ADMIN")]
    public void An_unknown_saved_mode_falls_back_to_the_default_not_to_a_prompt_loop(string saved)
    {
        Assert.Equal(StartupAction.Elevate, Decide(saved: saved));
        Assert.Equal(StartupAction.Continue, Decide(saved: saved, legacy: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("admin")]
    [InlineData("ask")]
    [InlineData("normal")]
    public void A_process_that_is_already_elevated_never_asks(string? saved)
    {
        Assert.Equal(StartupAction.Continue, Decide(saved, elevated: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("admin")]
    [InlineData("ask")]
    public void The_copy_an_elevation_restart_started_never_restarts_again(string? saved)
    {
        // Whatever the restart's outcome, --relaunched ends the chain: no second prompt, no loop.
        Assert.Equal(StartupAction.Continue, Decide(saved, relaunched: true));
        Assert.Equal(StartupAction.Continue, Decide(saved, relaunched: true, folderProtected: Shared));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("admin")]
    [InlineData("ask")]
    public void No_elevate_never_asks_so_scripts_tests_and_launches_for_other_programs_stay_quiet(string? saved)
    {
        Assert.Equal(StartupAction.Continue, Decide(saved, noElevate: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("admin")]
    [InlineData("ask")]
    public void The_tray_started_at_sign_in_never_asks_so_there_is_no_prompt_at_logon(string? saved)
    {
        Assert.Equal(StartupAction.Continue, Decide(saved, tray: true));
    }

    [Fact]
    public void A_copy_in_a_folder_any_program_can_change_never_elevates_and_says_so_once()
    {
        // The default cannot be honoured there (docs/security.md): explained once, as the old "ask" was.
        Assert.Equal(StartupAction.ExplainSharedFolder, Decide(folderProtected: Shared));
        Assert.Equal(StartupAction.ExplainSharedFolder, Decide(saved: "ask", folderProtected: Shared));
        // Once the user said "don't show this again" (saved as normal), or an old saved "admin" travelled to such a folder, it just starts.
        Assert.Equal(StartupAction.Continue, Decide(saved: "normal", folderProtected: Shared));
        Assert.Equal(StartupAction.Continue, Decide(saved: "admin", folderProtected: Shared));
        Assert.Equal(StartupAction.Continue, Decide(saved: null, legacy: false, folderProtected: Shared));
    }

    [Fact]
    public void Declining_the_prompt_cannot_loop_because_the_decision_is_made_once_per_start()
    {
        // The window asks once (App.OnStartup, or the tray's first window); a declined prompt carries on unelevated and the same process
        // is never asked again. A restart that was accepted comes back as --relaunched and is not asked either: the whole chain is one prompt.
        var first = Decide();
        var afterAcceptedRestart = Decide(relaunched: true, elevated: true);
        Assert.Equal(StartupAction.Elevate, first);
        Assert.Equal(StartupAction.Continue, afterAcceptedRestart);
        var afterDeclinedRestartWithRelaunchedFlag = Decide(relaunched: true);
        Assert.Equal(StartupAction.Continue, afterDeclinedRestartWithRelaunchedFlag);
    }
}
