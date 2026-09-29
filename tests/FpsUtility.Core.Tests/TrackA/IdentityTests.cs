using FpsUtility.Core.TrackA;

namespace FpsUtility.Core.Tests.TrackA;

/// <summary>Cohort and session ids (spec §3): operator-assigned or clock-made, never derived from the machine.</summary>
public sealed class IdentityTests
{
    [Theory]
    [InlineData("PC-000")]
    [InlineData("PC-001")]
    [InlineData("PC-999")]
    public void A_cohort_id_is_pc_and_three_digits(string id) => Assert.True(CohortId.IsValid(id));

    [Theory]
    [InlineData("PCB-001")] // Track B, not Track A
    [InlineData("pc-001")]
    [InlineData("PC-1")]
    [InlineData("PC-0001")]
    [InlineData(" PC-001")]
    [InlineData("PC-001\n")]
    [InlineData("PC-00A")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? id)
    {
        Assert.False(CohortId.IsValid(id));
        Assert.Throws<ArgumentException>(() => CohortId.Require(id, "cohortId"));
    }

    [Fact]
    public void A_session_id_is_the_utc_start_time_to_the_second()
    {
        Assert.Equal("session-20260929T120000Z", SessionId.At(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(SessionId.IsValid("session-20260929T120000Z"));
        Assert.Throws<ArgumentException>(() => SessionId.At(new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.FromHours(2))));
    }

    [Theory]
    [InlineData("session-20260929-01")]
    [InlineData("session-20261399T120000Z")] // no 13th month
    [InlineData("session-20260929T120000")]
    [InlineData("S-20260929T120000Z")]
    public void Malformed_session_ids_are_refused(string id) => Assert.False(SessionId.IsValid(id));

    [Fact]
    public void No_identity_comes_from_the_machine()
    {
        // The only inputs to a session's identity are the operator's cohort id and the clock: two different machines
        // captured at the same second under the same cohort id get the same ids, so ids can't encode hardware.
        var one = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(TrackAFixture.Raw())));
        var other = TrackAFixture.Package(TrackAFixture.Baseline(TrackAFixture.Prepare(TrackAFixture.Raw(change: sources => sources with
        {
            Pcie = Normalize.RawBuilder.Unavailable<Raw.PcieData>(Raw.MissingReason.AccessDenied),
        }))));

        Assert.Equal(one.Manifest.CohortId, other.Manifest.CohortId);
        Assert.Equal(one.Manifest.SessionId, other.Manifest.SessionId);
        Assert.Equal(typeof(DateTimeOffset), Assert.Single(typeof(SessionId).GetMethod(nameof(SessionId.At))!.GetParameters()).ParameterType);
    }

    [Fact]
    public void Two_sessions_of_one_cohort_id_are_one_machine_in_the_index()
    {
        var first = TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started)));
        var second = TrackAFixture.Package(TrackAFixture.Baseline(session: SessionId.At(TrackAFixture.Started.AddHours(1))));

        var index = CohortIndexTests.Index(first, second);

        var machine = Assert.Single(index.Machines);
        Assert.Equal(2, machine.Sessions.Count);
        Assert.Equal(1, index.Summary.Single(tally => tally.Dimension == "machines").Machines);
    }
}
