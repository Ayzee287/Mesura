using Mesura.Core.Setup;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Setup;

/// <summary>The run journal's format (0.8): it reads back what was written, survives a line cut off by a crash, refuses a header it can't read, and recovery keeps only what the PC still shows.</summary>
public sealed class RunJournalTests
{
    private static RecordedChange Reg(string name, string? before, string after) =>
        new(ChangeKind.Registry, $@"HKLM\SOFTWARE\Test\{name}", "HKLM", @"SOFTWARE\Test", name, RegistryKind.DWord, before, after, RegistryKind.DWord);

    private static string Journal(string operation = "windows-apply", params (string Op, RecordedChange Change)[] entries) =>
        string.Join('\n', new[] { RunJournal.HeaderLine("20261125T120000Z-" + operation, operation, "2026-11-25T12:00:00.0000000Z", 100, 5) }
            .Concat(entries.Select(e => RunJournal.EntryLine(e.Op, e.Op + " name", Activation.Reboot, e.Change)))) + "\n";

    [Fact]
    public void What_is_written_reads_back_exactly()
    {
        var change = Reg("A", null, "1") with { KeyCreated = true, UserSid = null };
        var parsed = RunJournal.Parse(Journal("windows-apply", ("op.a", change)))!;

        Assert.Equal("20261125T120000Z-windows-apply", parsed.Header.RunId);
        Assert.Equal(100, parsed.Header.ProcessId);
        Assert.Equal(5, parsed.Header.ProcessStartTicks);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal(change, entry.Change);
        Assert.Equal(Activation.Reboot, entry.Activation);
        Assert.False(parsed.Torn);
    }

    [Fact]
    public void A_last_line_cut_off_by_a_crash_is_ignored_and_reported()
    {
        var text = Journal("windows-apply", ("op.a", Reg("A", "0", "1")), ("op.b", Reg("B", "0", "1")));
        var cut = text[..(text.Length - 20)];

        var parsed = RunJournal.Parse(cut)!;

        Assert.True(parsed.Torn);
        Assert.Equal("op.a", Assert.Single(parsed.Entries).OperationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("{not json")]
    [InlineData("{\"kind\":\"Something.Else\",\"schemaVersion\":1,\"runId\":\"x\"}")]
    [InlineData("{\"kind\":\"Mesura.RunJournal\",\"schemaVersion\":99,\"runId\":\"x\",\"operation\":\"windows-apply\",\"startedUtc\":\"s\",\"processId\":1,\"processStartTicks\":1}")]
    public void A_header_that_cannot_be_read_gives_nothing_rather_than_a_guess(string text) =>
        Assert.Null(RunJournal.Parse(text));

    [Fact]
    public void Recovery_drops_what_was_never_made_and_keeps_what_is_in_place_or_cannot_be_told()
    {
        var made = Reg("Made", "0", "1");
        var never = Reg("Never", "0", "1");
        var unknown = Reg("Unknown", "0", "1");
        var parsed = RunJournal.Parse(Journal("windows-apply", ("op.a", made), ("op.a", never), ("op.b", unknown)))!;
        ChangeState Check(RecordedChange c) => c == made ? ChangeState.InPlace : c == never ? ChangeState.NotMade : ChangeState.Other;

        var record = RunJournal.Recover(parsed, Check, null, "2026-11-25T12:05:00.0000000Z", out var findings);

        Assert.NotNull(record);
        Assert.Equal(new RunJournalFindings(1, 1, 1, 0), findings);
        Assert.Equal("20261125T120000Z-windows-apply" + RunJournal.RecoveredSuffix, record.Id);
        Assert.Equal("windows-apply", record.Operation);
        Assert.True(record.WasInterrupted);
        Assert.Equal(["op.a", "op.b"], record.WindowsSteps.Select(s => s.OperationId));
        Assert.Equal([made], record.WindowsSteps[0].Changes);
        Assert.All(record.WindowsSteps, s => Assert.Equal(WindowsOpStatus.Applied, s.Status));
        Assert.Contains("Interrupted", record.WindowsSteps[0].Detail, StringComparison.Ordinal);

        // The record survives its own round trip, flag included, and an older record without the flag reads as not interrupted.
        Assert.True(SetupRun.FromJson(record.ToJson()).WasInterrupted);
        Assert.DoesNotContain("interrupted", SetupRun.Create("x", "windows-apply", null, "s", "f", [], []).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_journal_whose_changes_were_all_never_made_leaves_no_record()
    {
        var parsed = RunJournal.Parse(Journal("windows-apply", ("op.a", Reg("A", "0", "1"))))!;

        Assert.Null(RunJournal.Recover(parsed, _ => ChangeState.NotMade, null, "now", out var findings));
        Assert.Equal(1, findings.NotMade);
    }

    [Fact]
    public void An_interrupted_undo_is_recovered_as_an_undo()
    {
        var parsed = RunJournal.Parse(Journal("windows-revert", ("op.a", Reg("A", "1", "0"))))!;

        var record = RunJournal.Recover(parsed, _ => ChangeState.InPlace, null, "now", out _)!;

        Assert.Equal("windows-revert", record.Operation);
        Assert.Equal(WindowsOpStatus.Reverted, Assert.Single(record.WindowsSteps).Status);
    }

    [Fact]
    public void Entry_lines_are_single_lines()
    {
        var line = RunJournal.EntryLine("op.a", "Name with\nnewline", Activation.Immediate, Reg("A\nB", "0", "1"));

        Assert.DoesNotContain('\n', line);
    }
}
