using System.Diagnostics;
using System.Text.RegularExpressions;
using Mesura.Core.Windows;
using Mesura.Platform.Windows.Gpu;
using Mesura.Platform.Windows.Windows;

namespace Mesura.Setup.Windows.Gpu;

/// <summary>
/// The one owner of the process launch that lets the window use AMD's driver library without calling it in its own process (architecture §2.3: process launch only in its owner classes; its neighbours are
/// <c>WindowsWriter</c> and <c>SystemTools</c>). It starts Mesura's own <c>cli\mesura.exe</c>, shipped beside the window, with fixed words only: <c>gpu amd --json</c> to read, <c>gpu amd set antilag on|off
/// --gpu N --apply</c> to write, <c>gpu amd changes</c> and <c>gpu amd undo &lt;id&gt; --apply</c> to find and put back a change. The only variable parts are a GPU index and a change id the writer itself
/// made (checked against a pattern). A crash inside the driver ends that child process and the window goes on, and the writer's record, kept before the write, is how it is put back.
/// </summary>
internal static partial class AmdCommandLineRunner
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);   // a reading that does not come back in this time is "could not be read": the window is waiting for it
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Tests shorten the wait to prove that a command line that never finishes is ended.</summary>
    internal static TimeSpan? TimeoutOverride { get; set; }

    /// <summary>Tests replace the process launch: the arguments in, the exit code and everything printed out (<c>null</c>: it could not run).</summary>
    internal static Func<IReadOnlyList<string>, (int Exit, string Text)?>? RunOverride { get; set; }

    [GeneratedRegex(@"^Change (?<id>\d{8}T\d+Z-gpu-[a-z]+): (?<status>\w+)\.", RegexOptions.Multiline)]
    private static partial Regex ChangeLine();

    [GeneratedRegex(@"^(?<id>\d{8}T\d+Z-gpu-[a-z]+)\s", RegexOptions.Multiline)]
    private static partial Regex ChangeListLine();

    [GeneratedRegex(@"^\d{8}T\d+Z-gpu-[a-z]+$")]
    private static partial Regex ChangeId();

    /// <summary>Whether the command line is there to ask (the window's folder has <c>cli\mesura.exe</c>) or a test stands in for it.</summary>
    public static bool Available => RunOverride is not null || AmdCommandLine.Path is not null;

    /// <summary>What the AMD driver says about Anti-Lag, read by the command line; <c>null</c> when it could not be run or did not print the report.</summary>
    public static GpuFeatureReading? ReadAntiLag()
    {
        if (Run(["gpu", "amd", "--json"], ReadTimeout) is not { } answer)
        {
            return GpuFeatureReading.None(GpuAvailability.Unreadable, "The command line that reads the AMD driver could not be run.");
        }

        return AmdCommandLine.ParseReport(answer.Text) is { } report
            ? GpuFeatureReader.FromReport(report)
            : GpuFeatureReading.None(GpuAvailability.Unreadable, "The command line did not print the AMD report.");
    }

    /// <summary>Turns the original Anti-Lag on or off through the command line; returns the id of the writer's record and its words, or throws, after putting back what a crashed or unconfirmed run left.</summary>
    public static GpuWriteResult SetAntiLag(int gpuIndex, bool enabled)
    {
        var known = ChangeIds();
        var answer = Run(["gpu", "amd", "set", "antilag", enabled ? "on" : "off", "--gpu", gpuIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), "--apply"], WriteTimeout)
            ?? throw new InvalidOperationException("The command line that writes the AMD driver could not be run, so nothing was changed.");
        var match = ChangeLine().Match(answer.Text);
        if (match.Success && match.Groups["status"].Value == "Applied")
        {
            return new GpuWriteResult(match.Groups["id"].Value, Words(answer.Text));
        }

        // Not applied, or the command line ended without an answer (a crash inside the driver's library after the write): whatever record it made is put back.
        var ids = match.Success ? [match.Groups["id"].Value] : ChangeIds().Except(known, StringComparer.Ordinal).ToList();
        var putBack = string.Empty;
        foreach (var id in ids)
        {
            putBack += Run(["gpu", "amd", "undo", id, "--apply"], WriteTimeout) is { } undo && ChangeLine().Match(undo.Text) is { Success: true } undone && undone.Groups["status"].Value == "Reverted"
                ? " What the driver changed was put back."
                : $" Putting it back did not finish; mesura gpu amd undo {id} --apply tries again.";
        }

        var said = Words(answer.Text);
        throw new InvalidOperationException((said.Length > 0 ? said : $"The command line ended without an answer (exit code {answer.Exit}).") + putBack);
    }

    /// <summary>Puts a change back through the command line; throws unless it reports the change reverted (or nothing to undo).</summary>
    public static void UndoChange(string changeId)
    {
        if (!ChangeId().IsMatch(changeId))
        {
            throw new ArgumentException($"'{changeId}' is not a GPU change id.", nameof(changeId));
        }

        var answer = Run(["gpu", "amd", "undo", changeId, "--apply"], WriteTimeout)
            ?? throw new InvalidOperationException("The command line that writes the AMD driver could not be run, so the change was not put back.");
        var match = ChangeLine().Match(answer.Text);
        if (!match.Success || match.Groups["status"].Value is not ("Reverted" or "NotWritten"))
        {
            throw new InvalidOperationException($"The GPU change {changeId} was not put back: {Words(answer.Text)}");
        }
    }

    private static List<string> ChangeIds() =>
        Run(["gpu", "amd", "changes"], ReadTimeout) is { } answer ? [.. ChangeListLine().Matches(answer.Text).Select(m => m.Groups["id"].Value)] : [];

    /// <summary>The command line's own sentences without the id line and the preview chatter.</summary>
    private static string Words(string text) =>
        string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Change ", StringComparison.Ordinal) && !l.StartsWith("Preview only", StringComparison.Ordinal)
            && !l.StartsWith("Windows catalog:", StringComparison.Ordinal)));

    private static (int Exit, string Text)? Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        if (RunOverride is { } run)
        {
            return run(args);
        }

        if (AmdCommandLine.Path is not { } exe)
        {
            return null;
        }

        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

#pragma warning disable RS0030 // The one launch of Mesura's own command line for the AMD driver: fixed words, class remarks.
            using var process = Process.Start(info);
#pragma warning restore RS0030
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutOverride ?? timeout))
            {
                try
                {
#pragma warning disable RS0030 // Same owner: a command line that does not finish is ended.
                    process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
                }
                catch (InvalidOperationException)
                {
                    // it ended between the wait and the kill
                }

                return null;
            }

            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
