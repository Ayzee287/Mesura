using System.ComponentModel;
using Mesura.Updating;

namespace Mesura.Updating.Tests;

/// <summary>
/// Moving between the per-user and the all-users copy with consent (0.5). These pin the decisions (which copy may be offered, what is refused, how each
/// outcome is reported and that this copy's own registration is checked afterwards); they do not run Windows Installer. The real removal is the VM scenario
/// in docs/hardware-test-plan-0.5.md.
/// </summary>
public sealed class CopyTransitionTests
{
    private const string UserCode = "{11111111-AAAA-BBBB-CCCC-111111111111}";
    private const string MachineCode = "{22222222-AAAA-BBBB-CCCC-222222222222}";
    private const string UserFolder = @"C:\Users\Ann\AppData\Local\Programs\Mesura";
    private const string MachineFolder = @"C:\Program Files\Mesura";
    private static readonly string UserExe = UserFolder + @"\Mesura.exe";
    private static readonly string MachineExe = MachineFolder + @"\Mesura.exe";

    private static InstalledCopy User(string code = UserCode) => new(InstallScope.User, code, UserFolder, "0.4.4");

    private static InstalledCopy Machine(string code = MachineCode) => new(InstallScope.Machine, code, MachineFolder, "0.4.5");

    private sealed class Registrations(List<InstalledCopy> copies) : IInstalledCopies
    {
        public IReadOnlyList<InstalledCopy> Read() => [.. copies];
    }

    private sealed class Uninstaller(List<InstalledCopy> copies, int exitCode = 0, bool removes = true, Exception? throws = null, string? alsoRemoves = null) : ICopyUninstaller
    {
        public List<(string Code, bool Elevate)> Calls { get; } = [];

        public int Uninstall(string productCode, bool elevate)
        {
            Calls.Add((productCode, elevate));
            if (throws is not null)
            {
                throw throws;
            }

            if (removes)
            {
                copies.RemoveAll(c => c.ProductCode == productCode);
            }

            if (alsoRemoves is not null)
            {
                copies.RemoveAll(c => c.ProductCode == alsoRemoves);
            }

            return exitCode;
        }
    }

    [Fact]
    public void A_copy_of_the_other_scope_with_its_own_product_code_is_offered_for_removal()
    {
        var plan = CopyTransition.Plan(MachineExe, [User(), Machine()]);

        Assert.Equal(TransitionKind.Offer, plan.Kind);
        Assert.Equal(User(), plan.Other);
        Assert.Equal(TransitionKind.Offer, CopyTransition.Plan(UserExe, [User(), Machine()]).Kind);
        Assert.Equal(Machine(), CopyTransition.Plan(UserExe, [User(), Machine()]).Other);
    }

    [Fact]
    public void Nothing_is_offered_for_a_single_copy_or_for_a_copy_that_is_not_installed()
    {
        Assert.Equal(TransitionKind.None, CopyTransition.Plan(MachineExe, [Machine()]).Kind);
        Assert.Equal(TransitionKind.None, CopyTransition.Plan(@"D:\Portable\Mesura.exe", [User(), Machine()]).Kind); // a portable copy removes nothing
        Assert.Equal(TransitionKind.None, CopyTransition.Plan(MachineExe, []).Kind);
    }

    [Fact]
    public void One_product_code_in_both_scopes_or_two_other_copies_is_left_to_the_user_because_msiexec_could_remove_this_copy()
    {
        Assert.Equal(TransitionKind.Manual, CopyTransition.Plan(MachineExe, [User(MachineCode), Machine()]).Kind);
        Assert.Equal(TransitionKind.Manual, CopyTransition.Plan(MachineExe, [User(), User("{33333333-AAAA-BBBB-CCCC-333333333333}"), Machine()]).Kind);
        Assert.Equal(TransitionKind.Manual, CopyTransition.Plan(MachineExe, [new(InstallScope.User, UserCode, MachineFolder, "0.4.4"), Machine()]).Kind); // the same folder
    }

    [Fact]
    public void Removing_the_other_copy_runs_msiexec_for_its_product_code_and_checks_both_registrations_afterwards()
    {
        var copies = new List<InstalledCopy> { User(), Machine() };
        var uninstaller = new Uninstaller(copies);

        var result = CopyTransition.Remove(CopyTransition.Plan(MachineExe, copies), elevated: false, MachineExe, new Registrations(copies), uninstaller);

        Assert.Equal(RemovalStatus.Removed, result.Status);
        Assert.Equal([(UserCode, false)], uninstaller.Calls); // a per-user copy needs no administrator prompt
        Assert.Equal([Machine()], copies);
    }

    [Fact]
    public void An_all_users_copy_is_removed_with_an_administrator_prompt_unless_mesura_already_has_it()
    {
        var copies = new List<InstalledCopy> { User(), Machine() };
        var plain = new Uninstaller(copies);
        CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), elevated: false, UserExe, new Registrations(copies), plain);
        Assert.Equal([(MachineCode, true)], plain.Calls);

        copies = [User(), Machine()];
        var already = new Uninstaller(copies);
        CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), elevated: true, UserExe, new Registrations(copies), already);
        Assert.Equal([(MachineCode, false)], already.Calls);
    }

    [Theory]
    [InlineData(1602)]
    [InlineData(1223)]
    public void A_cancelled_uninstall_or_declined_prompt_changes_nothing_and_says_so(int code)
    {
        var copies = new List<InstalledCopy> { User(), Machine() };

        var result = CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, exitCode: code, removes: false));

        Assert.Equal(RemovalStatus.Cancelled, result.Status);
        Assert.Equal(2, copies.Count);
    }

    [Fact]
    public void A_declined_administrator_prompt_that_surfaces_as_an_exception_is_a_cancel_not_a_failure()
    {
        var copies = new List<InstalledCopy> { User(), Machine() };

        var result = CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, throws: new Win32Exception(1223)));

        Assert.Equal(RemovalStatus.Cancelled, result.Status);
    }

    [Fact]
    public void A_failed_uninstall_or_one_that_leaves_the_copy_registered_is_a_failure_and_both_copies_remain()
    {
        var copies = new List<InstalledCopy> { User(), Machine() };
        Assert.Equal(RemovalStatus.Failed, CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, exitCode: 1603, removes: false)).Status);
        Assert.Equal(RemovalStatus.Failed, CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, exitCode: 0, removes: false)).Status);
        Assert.Equal(RemovalStatus.Failed, CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, throws: new InvalidOperationException("msiexec didn't start."))).Status);
        Assert.Equal(2, copies.Count);
    }

    [Fact]
    public void If_the_running_copys_own_registration_is_gone_afterwards_it_is_reported_and_never_called_a_success()
    {
        var copies = new List<InstalledCopy> { User(), Machine() };

        // Windows Installer removed both (the product the user chose to keep as well).
        var result = CopyTransition.Remove(CopyTransition.Plan(UserExe, copies), false, UserExe, new Registrations(copies), new Uninstaller(copies, alsoRemoves: UserCode));

        Assert.Equal(RemovalStatus.OwnCopyLost, result.Status);
        Assert.Contains("installer again", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_that_is_not_an_offer_removes_nothing()
    {
        var copies = new List<InstalledCopy> { User(MachineCode), Machine() };
        var uninstaller = new Uninstaller(copies);

        var result = CopyTransition.Remove(CopyTransition.Plan(MachineExe, copies), false, MachineExe, new Registrations(copies), uninstaller);

        Assert.Equal(RemovalStatus.Failed, result.Status);
        Assert.Empty(uninstaller.Calls);
    }

    [Fact]
    public void A_product_code_that_is_not_a_guid_is_never_put_on_a_command_line()
    {
        var evil = new InstalledCopy(InstallScope.User, "{1} & calc", UserFolder, "0.4.4");
        var copies = new List<InstalledCopy> { evil, Machine() };
        var uninstaller = new Uninstaller(copies);

        var result = CopyTransition.Remove(new TransitionPlan(TransitionKind.Offer, evil), false, MachineExe, new Registrations(copies), uninstaller);

        Assert.Equal(RemovalStatus.Failed, result.Status);
        Assert.Empty(uninstaller.Calls);
    }

    [Fact]
    public void The_registry_reader_returns_only_well_formed_registrations_on_the_machine_running_the_tests()
    {
        var copies = new WindowsInstallerCopies().Read();

        Assert.All(copies, c =>
        {
            Assert.Matches(@"^\{[0-9A-F]{8}-([0-9A-F]{4}-){3}[0-9A-F]{12}\}$", c.ProductCode);
            Assert.False(c.Folder.EndsWith('\\'));
        });
    }
}
