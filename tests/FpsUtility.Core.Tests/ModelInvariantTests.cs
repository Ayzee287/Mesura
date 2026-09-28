using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Tests;

/// <summary>The rules §4.4 sets for readings, source entries and the envelope, enforced by the model itself.</summary>
public sealed class ModelInvariantTests
{
    [Fact]
    public void A_reading_is_either_known_or_missing_with_a_reason()
    {
        var known = Reading.Known(0);
        var missing = Reading.Missing<int>(MissingReason.NotSupported, "not on this build");

        Assert.True(known.IsKnown);
        Assert.Equal(0, known.Value);
        Assert.False(missing.IsKnown);
        Assert.Equal(MissingReason.NotSupported, missing.Missing);
    }

    [Fact]
    public void A_reading_cannot_be_known_and_missing_or_neither()
    {
        Assert.Throws<ArgumentException>(() => new Reading<string>(null, null, null, null));
        Assert.Throws<ArgumentException>(() => new Reading<int>(5, null, MissingReason.SourceFailed, "failed"));
        Assert.Throws<ArgumentException>(() => new Reading<string>("value", null, null, "a known value has no detail"));
    }

    [Fact]
    public void A_missing_reading_always_says_why()
    {
        Assert.Throws<ArgumentException>(() => Reading.Missing<int>(MissingReason.SourceFailed, " "));
    }

    [Fact]
    public void An_unrecognised_native_code_is_kept_on_a_missing_reading()
    {
        var reading = Reading.Missing<MediaKind>(MissingReason.ParseFailed, "MediaType 7 is not documented", 7);

        Assert.Equal(7, reading.Native);
        Assert.False(reading.IsKnown);
    }

    [Fact]
    public void A_source_is_either_ok_with_data_or_unavailable_with_a_reason()
    {
        var data = new PowerData(Reading.Known(true), Reading.Known(false));

        Assert.Throws<ArgumentException>(() => new SourceEntry<PowerData>(SourceStatus.Ok, "test", 1));
        Assert.Throws<ArgumentException>(() => new SourceEntry<PowerData>(SourceStatus.Ok, "test", 1, MissingReason.SourceFailed, "x", data));
        Assert.Throws<ArgumentException>(() => new SourceEntry<PowerData>(SourceStatus.Unavailable, "test", 1, data: data));
        Assert.Throws<ArgumentException>(() => new SourceEntry<PowerData>(SourceStatus.Unavailable, "test", 1, MissingReason.SourceFailed, ""));
        Assert.Throws<ArgumentException>(() => SourceEntry.Ok("", 1, data));
    }

    [Fact]
    public void A_snapshot_is_schema_version_1_and_captured_in_utc()
    {
        var valid = SnapshotFixture.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => new RawSnapshot(2, valid.CapturedAtUtc, valid.App, valid.Os, false, valid.Sources));
        Assert.Throws<ArgumentException>(() => new RawSnapshot(1, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3)), valid.App, valid.Os, false, valid.Sources));
    }

    [Fact]
    public void Lists_are_put_in_a_fixed_order_by_the_model()
    {
        var modes = Reading.Known<IReadOnlyList<DisplayMode>>(
        [
            new DisplayMode(2560, 1440, 165, false),
            new DisplayMode(1920, 1080, 60, false),
            new DisplayMode(2560, 1440, 60, false),
        ]);

        var output = new DisplayOutput("A", "0", "1", Reading.Known("S"), Reading.Known(ConnectionKind.Hdmi, 5), Reading.Known(new Resolution(1, 1)), Reading.Known(new Rational(1, 1)), Reading.Known(new Rational(1, 1)), Reading.Known("M"), Reading.Known("P"), modes);

        Assert.Equal([1920, 2560, 2560], output.OfferedModes.Value!.Select(mode => mode.Width));
        Assert.Equal([60, 60, 165], output.OfferedModes.Value!.Select(mode => mode.RefreshHz));
    }
}
