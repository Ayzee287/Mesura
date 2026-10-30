using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The undo of a run is read from a file in the user's data folder, which any program running as the user can write. These tests forge
/// such a record and show (1) that without the catalog check an elevated undo writes wherever the record says (the weakness), and
/// (2) that with it the undo refuses and writes nothing (the fix). docs/security.md.
/// </summary>
public sealed class RevertTrustTests
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static readonly WindowsCatalog Catalog = WindowsCatalog.Parse("""
        { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [
          { "id": "test.policy", "name": "Policy", "category": "System", "description": "d", "risk": "systemConfiguration",
            "registry": [ { "hive": "HKLM", "path": "SOFTWARE\\Policies\\Test", "name": "Flag", "type": "dword", "value": "1" } ],
            "services": [ { "name": "TestSvc", "start": "disabled" } ] }
        ] }
        """);

    private static WindowsOpOutcome Record(params RecordedChange[] changes) =>
        new("test.policy", "Policy", WindowsOpStatus.Applied, "", Activation.Immediate, changes);

    /// <summary>A forged "before" for a value the operation never touches: after the undo, a Run entry would start the attacker's program with administrator rights.</summary>
    private static RecordedChange ForgedRunEntry() =>
        new(ChangeKind.Registry, @"HKLM\" + RunKey + @"\updater", "HKLM", RunKey, "updater", null, @"C:\Profiles\other\AppData\Local\evil.exe", null, RegistryKind.String);

    private static RecordedChange Genuine() =>
        new(ChangeKind.Registry, @"HKLM\SOFTWARE\Policies\Test\Flag", "HKLM", @"SOFTWARE\Policies\Test", "Flag", RegistryKind.DWord, "0", "1", RegistryKind.DWord);

    [Fact]
    public void Without_the_catalog_check_an_elevated_undo_writes_wherever_a_forged_record_points()
    {
        var pc = new FakeWindows { IsElevated = true };

        var outcome = new WindowsConfigurator(pc, pc).Revert([Record(ForgedRunEntry())]).Single();

        // The weakness this release closes: nothing about the record was checked, the registry write happened.
        Assert.Contains(pc.Writes, w => w.Contains("Run", StringComparison.OrdinalIgnoreCase) || w.Contains("updater", StringComparison.OrdinalIgnoreCase) || pc.Registry.Keys.Any(k => k.Contains("updater", StringComparison.OrdinalIgnoreCase)));
        Assert.NotEqual(WindowsOpStatus.Blocked, outcome.Status);
    }

    [Fact]
    public void With_the_catalog_check_a_forged_registry_target_is_refused_and_nothing_is_written()
    {
        var pc = new FakeWindows { IsElevated = true };
        var before = pc.Writes.Count;

        var outcome = new WindowsConfigurator(pc, pc, Catalog).Revert([Record(ForgedRunEntry())]).Single();

        Assert.Equal(WindowsOpStatus.Blocked, outcome.Status);
        Assert.Contains("Not undone", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(before, pc.Writes.Count);
        Assert.DoesNotContain(pc.Registry.Keys, k => k.Contains("updater", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_forged_service_is_refused()
    {
        var pc = new FakeWindows { IsElevated = true };
        pc.Services["WinDefend"] = 4;
        var forged = new RecordedChange(ChangeKind.Service, "WinDefend", null, null, "WinDefend", null, "2", "4");

        var outcome = new WindowsConfigurator(pc, pc, Catalog).Revert([Record(forged)]).Single();

        Assert.Equal(WindowsOpStatus.Blocked, outcome.Status);
        Assert.Equal(4, pc.Services["WinDefend"]);
    }

    [Fact]
    public void A_record_for_an_operation_this_catalog_does_not_have_is_refused()
    {
        var pc = new FakeWindows { IsElevated = true };
        var outcome = Record(Genuine()) with { OperationId = "made.up" };

        Assert.Equal(WindowsOpStatus.Blocked, new WindowsConfigurator(pc, pc, Catalog).Revert([outcome]).Single().Status);
    }

    [Fact]
    public void A_genuine_record_still_reverts_and_a_mixed_record_is_refused_whole()
    {
        var pc = new FakeWindows { IsElevated = true };
        pc.Registry[@"HKLM\SOFTWARE\Policies\Test\Flag"] = (RegistryKind.DWord, "1");

        Assert.Equal(WindowsOpStatus.Reverted, new WindowsConfigurator(pc, pc, Catalog).Revert([Record(Genuine())]).Single().Status);
        Assert.Equal("0", pc.Registry[@"HKLM\SOFTWARE\Policies\Test\Flag"].Value);

        pc.Registry[@"HKLM\SOFTWARE\Policies\Test\Flag"] = (RegistryKind.DWord, "1");
        var mixed = new WindowsConfigurator(pc, pc, Catalog).Revert([Record(Genuine(), ForgedRunEntry())]).Single();
        Assert.Equal(WindowsOpStatus.Blocked, mixed.Status);
        Assert.Equal("1", pc.Registry[@"HKLM\SOFTWARE\Policies\Test\Flag"].Value); // not even the genuine part ran
    }

    [Fact]
    public void A_per_adapter_target_matches_only_one_level_under_its_pattern()
    {
        var op = WindowsCatalog.Parse("""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [
              { "id": "test.each", "name": "Each", "category": "System", "description": "d", "risk": "systemConfiguration",
                "registryEach": [ { "hive": "HKLM", "pathPattern": "SYSTEM\\Tcpip\\Interfaces\\*", "name": "Ack", "type": "dword", "value": "1" } ] }
            ] }
            """).Operations.Single();
        RecordedChange At(string path) => new(ChangeKind.Registry, path, "HKLM", path, "Ack", RegistryKind.DWord, "0", "1");

        Assert.True(op.Declares(At(@"SYSTEM\Tcpip\Interfaces\{guid}")));
        Assert.False(op.Declares(At(@"SYSTEM\Tcpip\Interfaces\{guid}\Evil")));
        Assert.False(op.Declares(At(@"SYSTEM\Tcpip\Interfaces\")));
        Assert.False(op.Declares(At(@"SYSTEM\Other\{guid}")));
    }

    [Fact]
    public void Every_record_the_built_in_catalog_can_make_is_accepted_by_its_own_check()
    {
        // A change made by applying each registry setting of each built-in operation must be declared by that operation, or a genuine undo would be refused.
        var catalog = WindowsCatalog.Default;
        foreach (var op in catalog.Operations)
        {
            foreach (var r in op.Registry)
            {
                var change = new RecordedChange(ChangeKind.Registry, r.Target, r.Hive, r.Path, r.Name, r.Kind, "0", r.Value);
                Assert.True(op.Declares(change), $"{op.Id}: {r.Target}");
            }

            foreach (var s in op.Services)
            {
                Assert.True(op.Declares(new RecordedChange(ChangeKind.Service, s.Name, null, null, s.Name, null, "2", "4")), $"{op.Id}: service {s.Name}");
            }
        }
    }
}
