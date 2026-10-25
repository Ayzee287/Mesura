using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// The 2026-10-25 profile review (docs/profile-review-2026-10.md): how a stored value is known (documented, verified,
/// community), the Pro-and-higher requirement for machine policies, the official diagnostic-data policy, and the
/// separation of Microsoft's supported controls from the unofficial service and task switches.
/// </summary>
public sealed class ProfileReviewTests
{
    private static readonly WindowsCatalog Catalog = WindowsCatalog.Default;
    private const string AllowTelemetry = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry";

    private static WindowsOperation Op(string id) => Catalog.ById(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void The_diagnostic_data_policy_sets_required_never_security_and_only_where_windows_honours_it()
    {
        var op = Op("privacy.diagnostic-data-required");

        var setting = Assert.Single(op.Registry);
        Assert.Equal(("HKLM", "AllowTelemetry", "1"), (setting.Hive, setting.Name, setting.Value)); // 0 is Security: Enterprise only, equal to 1 elsewhere
        Assert.Equal(ValueBasis.Documented, op.Basis);
        Assert.Contains(DeviceRequirement.ProEdition, op.Requirements);
        Assert.Equal(OperationImpact.Privacy, op.Impact);

        var pro = new FakeWindows { PolicyEdition = true };
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, pro).State);
        var applied = new WindowsConfigurator(pro, pro).Apply([op], TestContext.Current.CancellationToken).Single();
        Assert.Equal(WindowsOpStatus.Applied, applied.Status);
        Assert.Equal("1", pro.Registry[AllowTelemetry].Value);
        Assert.All(new WindowsConfigurator(pro, pro).Revert([applied]), r => Assert.Equal(WindowsOpStatus.Reverted, r.Status));
        Assert.False(pro.Registry.ContainsKey(AllowTelemetry)); // put back to not set
    }

    [Fact]
    public void On_windows_home_a_machine_policy_is_not_applicable_and_never_written_and_an_unread_edition_leaves_it_alone()
    {
        var policy = Op("privacy.diagnostic-data-required");
        var home = new FakeWindows { PolicyEdition = false };

        var evaluation = WindowsEvaluator.Evaluate(policy, home);
        Assert.Equal(OperationState.Inapplicable, evaluation.State);
        Assert.Contains("Home", evaluation.Reason, StringComparison.Ordinal);
        Assert.Equal(WindowsOpStatus.Inapplicable, new WindowsConfigurator(home, home).Apply([policy], TestContext.Current.CancellationToken).Single().Status);
        Assert.Empty(home.Writes);

        var unread = new FakeWindows { PolicyEdition = null };
        Assert.Equal(OperationState.Unknown, WindowsEvaluator.Evaluate(policy, unread).State);
        Assert.DoesNotContain(new WindowsConfigurator(unread, unread).Apply([policy], TestContext.Current.CancellationToken), o => o.Status == WindowsOpStatus.Applied);
        Assert.Empty(unread.Writes);

        // The per-user entries of Recommended don't depend on the edition.
        Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(Op("privacy.no-advertising-id"), home).State);
    }

    [Fact]
    public void Every_machine_policy_listed_for_pro_and_higher_carries_the_requirement()
    {
        foreach (var id in new[] { "privacy.diagnostic-data-required", "privacy.no-activity-history", "privacy.no-cross-device-experiences",
                                   "updates.no-p2p-delivery", "taskbar.widgets-policy-off" })
        {
            Assert.Contains(DeviceRequirement.ProEdition, Op(id).Requirements);
        }
    }

    [Fact]
    public void The_unofficial_telemetry_switches_are_extras_of_maximum_and_never_in_a_profile_itself()
    {
        var services = Op("services.telemetry-off");
        Assert.Equal(["DiagTrack"], services.Services.Select(s => s.Name)); // the MDM message-routing service stays alone
        Assert.Contains("dmwappushservice", services.Limitations, StringComparison.Ordinal);
        Assert.Contains("not a control", services.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not a documented Microsoft control", Op("tasks.telemetry-off").Limitations, StringComparison.Ordinal);

        var maximum = Catalog.PresetById("maximum-performance")!;
        Assert.All(new[] { "services.telemetry-off", "tasks.telemetry-off" }, id =>
        {
            Assert.Contains(id, maximum.OptionalIds);
            Assert.DoesNotContain(Catalog.Presets, p => p.OperationIds.Contains(id)); // in no preset's own selection
        });
    }

    [Fact]
    public void Maximum_performance_keeps_every_recommended_entry_and_its_extras_are_unticked_and_distinct()
    {
        var recommended = Catalog.PresetById("recommended-gaming")!;
        var maximum = Catalog.PresetById("maximum-performance")!;

        Assert.All(recommended.OperationIds, id => Assert.Contains(id, maximum.OperationIds));
        Assert.Empty(maximum.OptionalIds.Intersect(maximum.OperationIds));
        Assert.Equal(maximum.OperationIds.Count, maximum.OperationIds.Distinct().Count()); // no setting twice
        Assert.Empty(WindowsConfigurator.Conflicts(Catalog.Resolve(maximum).Concat(maximum.OptionalIds.Select(Op)).ToList()));
    }

    [Fact]
    public void Verified_entries_say_what_was_checked_and_what_was_not()
    {
        var verified = Catalog.Operations.Where(o => o.Basis == ValueBasis.Verified).ToList();

        Assert.NotEmpty(verified);
        Assert.All(verified, o =>
        {
            Assert.Contains("Verified read-only", o.Source, StringComparison.Ordinal);
            Assert.Contains("build 26200", o.Source, StringComparison.Ordinal);
            Assert.True(o.Risk == RiskClass.UserPreference, $"{o.Id}: a verified-only value is a per-user preference, never a machine change");
            Assert.True(o.Registry.All(r => r.Hive == "HKCU"), o.Id);
        });
    }

    [Theory]
    [InlineData("""{ "valueBasis": "proven" }""", "unknown valueBasis 'proven'")]
    [InlineData("""{ "requires": [ "proedition", "linux" ] }""", "unknown requirement 'linux'")]
    public void An_unknown_basis_or_requirement_is_refused(string extra, string expected)
    {
        var operation = $$"""
            { "id": "a.b", "name": "A", "description": "D", "risk": "userPreference", {{extra.Trim('{', '}', ' ')}},
              "registry": [ { "hive": "HKCU", "path": "Software\\X", "name": "V", "type": "dword", "value": "1" } ] }
            """;
        var e = Assert.Throws<FormatException>(() => WindowsCatalog.Parse($$"""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 3, "operations": [ {{operation}} ], "presets": [] }
            """));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_basis_and_the_pro_requirement_are_read()
    {
        var catalog = WindowsCatalog.Parse("""
            { "kind": "Mesura.WindowsCatalog", "schemaVersion": 3,
              "operations": [ { "id": "a.b", "name": "A", "description": "D", "risk": "systemConfiguration", "valueBasis": "Verified", "requires": [ "proEdition" ],
                                "registry": [ { "hive": "HKLM", "path": "Software\\X", "name": "V", "type": "dword", "value": "1" } ] } ],
              "presets": [] }
            """);

        var op = catalog.ById("a.b")!;
        Assert.Equal(ValueBasis.Verified, op.Basis);
        Assert.Equal([DeviceRequirement.ProEdition], op.Requirements);
    }
}
