using System.Globalization;

namespace Mesura.Core.Nvidia;

/// <summary>One row of a list in NVCleanstall's window: its label, whether its check box is ticked (<c>null</c> for a heading), and whether the check box can be changed.</summary>
public sealed record NvListEntry(string Name, bool? Checked, bool CanChange);

/// <summary>
/// What Mesura needs from NVCleanstall's window, whatever reads it (UI Automation in the real program, a recorded page in tests). Everything is addressed by the
/// AutomationId NVCleanstall 1.19.0 gives its controls (measured in Windows Sandbox, 2026-11-02, docs/sandbox-results) or by the visible label of a list row; nothing is
/// addressed by position.
/// </summary>
public interface INvCleanstallWindow
{
    /// <summary>False once the window is gone (NVCleanstall closed or crashed).</summary>
    bool IsOpen { get; }

    /// <summary>The page's title ("Select Components To Install"), or empty while the page is still being built.</summary>
    string PageTitle { get; }

    bool Has(string automationId);

    bool IsEnabled(string automationId);

    /// <summary>The label of a button or the text of a text box.</summary>
    string Text(string automationId);

    void Choose(string automationId);

    void SetText(string automationId, string text);

    void Press(string automationId);

    IReadOnlyList<NvListEntry> ListEntries(string automationId);

    void SetChecked(string listAutomationId, string entryName, bool on);

    /// <summary>Whether a check box found by its AutomationId is ticked.</summary>
    bool IsTicked(string automationId);

    /// <summary>Ticks or unticks a check box found by its AutomationId.</summary>
    void Tick(string automationId, bool on);

    /// <summary>
    /// Whether NVCleanstall's "Rebuild digital signature" box is ticked (it is disabled and ticked by NVCleanstall itself when a selected tweak needs it, so it is only ever read), or
    /// <c>null</c> when the page has no such box.
    /// </summary>
    bool? SignatureRebuildTicked();
}

/// <summary>The AutomationIds and page titles of NVCleanstall 1.19.0 that the automation uses.</summary>
public static class NvCleanstallIds
{
    public const string SourcePage = "Select Driver Version To Install";
    public const string ComponentsPage = "Select Components To Install";
    public const string PreparingPage = "Preparing Sources";
    public const string TweaksPage = "Installation Tweaks";
    public const string FinishedPage = "Finished";
    public const string RunInstaller = "RunInstaller";
    public const string BuildPackage = "BuildSFX";
    public const string ExpertTweaks = "ShowExpertOptions";

    /// <summary>
    /// NVCleanstall's check box for each tweak of <see cref="NvidiaTweaks"/>, by AutomationId (read from the "Installation Tweaks" page of 1.19.0). "Rebuild digital signature" has none: the
    /// box is disabled and NVCleanstall ticks it itself when another ticked tweak needs it, so it is reported, not set. The Interrupt Policy and Priority lists are left at Default, as the
    /// configuration says.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TweakBoxes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["installer-telemetry"] = "DisableInstallerTelemetry",
        ["unattended"] = "Unattended",
        ["clean-install"] = "CleanInstall",
        ["disable-mpo"] = "DisableMPO",
        ["show-expert"] = "ShowExpertOptions",
        ["driver-telemetry"] = "DisableDriverTelemetry",
        ["hdaudio-sleep"] = "DisableHDAudioSleepTimer",
        ["msi"] = "EnableMSI",
        ["disable-hdcp"] = "DisableHDCP",
        ["add-hardware"] = "AddHardwareId",
        ["dlss-indicator"] = "ShowDlssIndicator",
        ["disable-ansel"] = "DisableNvCamera",
        ["disable-container"] = "DisableNvContainer",
        ["nvenc-patch"] = "NvEncPatch",
        ["eac-signing"] = "RebuildSignatureEAC",
        ["accept-unsigned"] = "SkipUnsignedDriverWarning",
        ["external-app"] = "RunProgram",
    };

    public const string RadioBest = "RadioAutodetect";
    public const string RadioPick = "RadioPickVersion";
    public const string RadioDisk = "RadioHaveDisk";
    public const string DiskPath = "TextHaveDisk";
    public const string Next = "ButtonNext";
    public const string Back = "ButtonPrev";
    public const string ComponentList = "List";
    public const string ComponentsAll = "ButtonAll";
    public const string ComponentsRecommended = "ButtonRecommended";
    public const string ComponentsMinimum = "ButtonMinimum";

    /// <summary>NVCleanstall prints "Microsoft Visual C 2017 Runtimes" for what its own documentation calls Visual C++: labels are compared without punctuation.</summary>
    public static string Normalize(string label) => new string([.. (label ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>How an automated run of NVCleanstall ended.</summary>
public enum NvRunStatus
{
    /// <summary>The pages Mesura knows were completed and NVCleanstall is now past them (see <see cref="NvRunResult.StoppedAt"/>).</summary>
    Completed,

    /// <summary>A page Mesura has no automation for, or a control that was not there: nothing further was pressed.</summary>
    StoppedAtUnknownPage,

    /// <summary>A control was set but NVCleanstall did not keep it (read back different), or the window went away.</summary>
    Failed,
}

/// <param name="TweaksTicked">The tweaks (by Mesura's ids) that were ticked on the tweaks page when Mesura left it, read back; empty when that page was not reached.</param>
/// <param name="SignatureRebuild">What the "Rebuild digital signature" box read on that page (<c>null</c>: not read).</param>
public sealed record NvRunResult(NvRunStatus Status, string StoppedAt, string Detail, IReadOnlyList<string> Steps, IReadOnlyList<string>? TweaksTicked = null, bool? SignatureRebuild = null);

/// <summary>
/// What the user is shown, and what must hold, before Install is pressed (the owner's conditions, 2026-11-02): a restore point, the NVIDIA profile backup, the list of tweaks as NVCleanstall
/// has them ticked, the state of "Rebuild digital signature", and then a separate confirmation. Every line is a fact read, not a promise.
/// </summary>
public sealed record NvInstallChecklist(
    string RestorePoint,
    bool RestorePointOk,
    string ProfileBackup,
    bool ProfileBackupOk,
    IReadOnlyList<string> TweaksExpected,
    IReadOnlyList<string> TweaksTicked,
    bool SignatureRequired,
    bool? SignatureRebuild,
    string Package,
    string DriverAtStart,
    string? DriverTarget)
{
    /// <summary>The tweaks NVCleanstall has ticked are exactly the configuration's.</summary>
    public bool TweaksMatch => TweaksExpected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(TweaksTicked.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>"Rebuild digital signature" is on when the configuration wants it (and cannot be unknown then).</summary>
    public bool SignatureOk => !SignatureRequired || SignatureRebuild == true;

    /// <summary>Why Install must not be pressed yet, one sentence each; empty when everything the owner asked for holds.</summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var list = new List<string>();
            if (!RestorePointOk) { list.Add($"No restore point: {RestorePoint}"); }
            if (!ProfileBackupOk) { list.Add($"The NVIDIA profile backup is not in place: {ProfileBackup}"); }
            if (!TweaksMatch) { list.Add($"NVCleanstall's tweaks are not the configuration's (ticked: {string.Join(", ", TweaksTicked)})."); }
            if (!SignatureOk) { list.Add($"\"Rebuild digital signature\" is {(SignatureRebuild is null ? "not on the page" : "not ticked")}, and the configuration needs it."); }
            return list;
        }
    }

    public bool CanInstall => Blockers.Count == 0;
}

/// <summary>
/// The automation of NVCleanstall's first pages: choose the driver files (the official package already on disk), tick exactly the configuration's components, move on. It
/// reads before it acts and reads back after, on every control, and stops at the first page it does not know instead of pressing anything.
/// </summary>
public static class NvCleanstallPages
{
    public static NvRunResult Run(INvCleanstallWindow window, NvidiaDriverConfig config, string driverPackagePath, Func<TimeSpan, bool> wait)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(wait);
        var steps = new List<string>();
        var normal = NvidiaDriverConfig.Normalize(config);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string>? ticked = null;
        bool? signature = null;
        for (var pageCount = 0; pageCount < 12; pageCount++)
        {
            if (!window.IsOpen)
            {
                return new(NvRunStatus.Failed, "", "NVCleanstall's window is gone.", steps);
            }

            var title = window.PageTitle;
            switch (title)
            {
                case "":
                    if (!wait(TimeSpan.FromSeconds(2))) { return new(NvRunStatus.Failed, "", "The page never appeared.", steps); }
                    pageCount--;
                    continue;
                case NvCleanstallIds.SourcePage:
                    if (!seen.Add(title)) { return new(NvRunStatus.Failed, title, "NVCleanstall stayed on the driver page after Next (it did not accept the driver files).", steps); }
                    if (Source(window, driverPackagePath, steps) is { } sourceError) { return new(NvRunStatus.Failed, title, sourceError, steps); }
                    break;
                case NvCleanstallIds.ComponentsPage:
                    if (!seen.Add(title)) { return new(NvRunStatus.Failed, title, "NVCleanstall stayed on the components page after Next.", steps); }
                    if (Components(window, normal, steps) is { } componentError) { return new(NvRunStatus.Failed, title, componentError, steps); }
                    break;
                case NvCleanstallIds.PreparingPage:
                    if (!wait(TimeSpan.FromSeconds(5))) { return new(NvRunStatus.Failed, title, "Stopped waiting while NVCleanstall prepared the package.", steps); }
                    pageCount--;
                    continue;
                case NvCleanstallIds.TweaksPage:
                    if (!seen.Add(title)) { return new(NvRunStatus.Failed, title, "NVCleanstall stayed on the tweaks page after Next.", steps); }
                    if (Tweaks(window, normal, steps) is { } tweakError) { return new(NvRunStatus.Failed, title, tweakError, steps); }
                    ticked = [.. NvCleanstallIds.TweakBoxes.Where(t => window.Has(t.Value) && window.IsTicked(t.Value)).Select(t => t.Key)];
                    signature = window.SignatureRebuildTicked();
                    break;
                case NvCleanstallIds.FinishedPage:
                    // The customized installer is ready. Mesura stops here: what is pressed next (Install, which starts NVIDIA's installer) is a separate, confirmed step.
                    steps.Add("finished: the customized installer is ready");
                    return new(NvRunStatus.Completed, title, "NVCleanstall has built the customized installer; nothing was installed.", steps, ticked, signature);
                default:
                    return new(NvRunStatus.StoppedAtUnknownPage, title, $"Page '{title}' has no automation yet; nothing was pressed on it.", steps);
            }

            if (!window.Has(NvCleanstallIds.Next) || !window.IsEnabled(NvCleanstallIds.Next) || window.Text(NvCleanstallIds.Next) != "Next")
            {
                return new(NvRunStatus.Failed, title, "The Next button is missing, disabled or no longer called Next.", steps);
            }

            window.Press(NvCleanstallIds.Next);
            steps.Add($"{title}: Next");

            // NVCleanstall moves to the next page a moment later (measured: up to a few seconds): wait for the title to change, up to a minute, before judging that it did not.
            for (var tick = 0; tick < 60 && window.IsOpen && window.PageTitle == title; tick++)
            {
                if (!wait(TimeSpan.FromSeconds(1))) { return new(NvRunStatus.Failed, title, "Interrupted.", steps); }
            }
        }

        return new(NvRunStatus.Failed, "", "Too many pages.", steps);
    }

    /// <summary>
    /// On the finished page, presses Install: NVCleanstall starts NVIDIA's installer, which replaces the display driver. Only call it after the user has confirmed that, and only on a page
    /// that reads "Finished" with an enabled Install button.
    /// </summary>
    public static string? PressInstall(INvCleanstallWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsOpen || window.PageTitle != NvCleanstallIds.FinishedPage || !window.Has(NvCleanstallIds.RunInstaller) || !window.IsEnabled(NvCleanstallIds.RunInstaller))
        {
            return "NVCleanstall is not on its finished page with an enabled Install button, so nothing was pressed.";
        }

        window.Press(NvCleanstallIds.RunInstaller);
        return null;
    }

    private static string? Tweaks(INvCleanstallWindow window, NvidiaDriverConfig config, List<string> steps)
    {
        var wanted = config.Tweaks.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The expert tweaks only exist on screen once this one is ticked, so it goes first.
        var order = NvCleanstallIds.TweakBoxes.OrderBy(t => t.Key == "show-expert" ? 0 : 1).ToList();
        foreach (var (tweak, box) in order)
        {
            var on = wanted.Contains(tweak);
            if (!window.Has(box))
            {
                if (on) { return $"The tweak '{tweak}' wants the check box '{box}', which is not on the page."; }
                continue;
            }

            if (window.IsTicked(box) == on)
            {
                continue;
            }

            if (!window.IsEnabled(box))
            {
                return $"'{box}' needs to be {(on ? "ticked" : "unticked")} but cannot be changed.";
            }

            window.Tick(box, on);
            if (window.IsTicked(box) != on)
            {
                return $"NVCleanstall did not keep '{box}' {(on ? "ticked" : "unticked")}.";
            }
        }

        // Read back every mapped box once more: the page must show exactly the configuration.
        foreach (var (tweak, box) in NvCleanstallIds.TweakBoxes.Where(t => window.Has(t.Value)))
        {
            if (window.IsTicked(box) != wanted.Contains(tweak))
            {
                return $"After the changes '{box}' is {(window.IsTicked(box) ? "ticked" : "unticked")} but the configuration says {(wanted.Contains(tweak) ? "ticked" : "unticked")}.";
            }
        }

        var ticked = NvCleanstallIds.TweakBoxes.Where(t => window.Has(t.Value) && window.IsTicked(t.Value)).Select(t => t.Key);
        steps.Add(string.Create(CultureInfo.InvariantCulture, $"tweaks ticked: {string.Join(", ", ticked)}"));
        if (wanted.Contains("rebuild-signature"))
        {
            steps.Add("rebuild digital signature: NVCleanstall ticks it itself when a selected tweak needs it (not set here; read at the end of the page for the gate before Install)");
        }

        return null;
    }

    private static string? Source(INvCleanstallWindow window, string path, List<string> steps)
    {
        if (!window.Has(NvCleanstallIds.RadioDisk) || !window.Has(NvCleanstallIds.DiskPath))
        {
            return "The 'Use driver files on disk' choice is not on the page.";
        }

        window.Choose(NvCleanstallIds.RadioDisk);
        if (!window.IsEnabled(NvCleanstallIds.DiskPath))
        {
            return "The driver path box did not become available after choosing 'Use driver files on disk'.";
        }

        window.SetText(NvCleanstallIds.DiskPath, path);
        if (!string.Equals(window.Text(NvCleanstallIds.DiskPath), path, StringComparison.OrdinalIgnoreCase))
        {
            return "NVCleanstall did not keep the driver path.";
        }

        steps.Add($"driver files on disk: {path}");
        return null;
    }

    private static string? Components(INvCleanstallWindow window, NvidiaDriverConfig config, List<string> steps)
    {
        var wanted = config.Components.Select(id => NvidiaComponents.ById(id)).Where(c => c is not null).Select(c => NvCleanstallIds.Normalize(c!.Label)).ToHashSet();
        foreach (var entry in window.ListEntries(NvCleanstallIds.ComponentList))
        {
            if (entry.Checked is null)
            {
                continue; // a heading ("NV App Components")
            }

            var on = wanted.Contains(NvCleanstallIds.Normalize(entry.Name.Replace(" (required)", "", StringComparison.Ordinal)));
            if (!entry.CanChange)
            {
                if (!entry.Checked.Value && on) { return $"'{entry.Name}' cannot be ticked but the configuration needs it."; }
                continue; // the display driver: required, always ticked
            }

            if (entry.Checked.Value != on)
            {
                window.SetChecked(NvCleanstallIds.ComponentList, entry.Name, on);
            }
        }

        // Read back: exactly the configuration's components are ticked.
        foreach (var entry in window.ListEntries(NvCleanstallIds.ComponentList).Where(e => e.Checked is not null))
        {
            var on = wanted.Contains(NvCleanstallIds.Normalize(entry.Name.Replace(" (required)", "", StringComparison.Ordinal)));
            if (entry.Checked!.Value != on && entry.CanChange)
            {
                return $"'{entry.Name}' is {(entry.Checked.Value ? "ticked" : "unticked")} after the change; the configuration wants it {(on ? "ticked" : "unticked")}.";
            }
        }

        var shown = window.ListEntries(NvCleanstallIds.ComponentList).Where(e => e.Checked == true).Select(e => e.Name);
        steps.Add(string.Create(CultureInfo.InvariantCulture, $"components ticked: {string.Join(", ", shown)}"));
        return null;
    }
}
