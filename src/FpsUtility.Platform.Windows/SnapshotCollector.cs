using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Platform.Windows.Collectors;

namespace FpsUtility.Platform.Windows;

/// <summary>
/// Collects a <see cref="RawSnapshot"/>: every source in parallel, each with its own time limit, each
/// failure turned into an <c>unavailable</c> entry with a reason (architecture §5 Collect, §10). No
/// exception from a source escapes, and no source blocks another. Collection only: nothing here judges
/// a value.
/// </summary>
public static class SnapshotCollector
{
    /// <summary>The per-source limit (architecture §10).</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    /// <param name="os">The OS facts the platform check already accepted.</param>
    /// <param name="app">The collecting app.</param>
    public static RawSnapshot Collect(OsInfo os, AppInfo app)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(app);

        var now = DateTimeOffset.UtcNow;
        var capturedAtUtc = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        var profile = new UserProfileRedactor(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var display = Run(DisplayCollector.Source, DisplayCollector.Collect, profile);
        var edid = Run(EdidCollector.Source, EdidCollector.Collect, profile);
        var adapters = Run(AdapterCollector.Source, AdapterCollector.Collect, profile);
        var smbios = Run(SmbiosCollector.Source, SmbiosCollector.Collect, profile);
        var pcie = Run(PcieCollector.Source, PcieCollector.Collect, profile);
        var storage = Run(StorageCollector.Source, () => StorageCollector.Collect(profile, SourceTimeout), profile);
        var steam = Run(SteamCollector.Source, () => SteamCollector.Collect(profile), profile);
        var power = Run(PowerCollector.Source, PowerCollector.Collect, profile);

        // Each task finishes by its own time limit, so this wait is bounded.
        Task.WaitAll(display, edid, adapters, smbios, pcie, storage, steam, power);

        return new RawSnapshot(
            RawSnapshot.CurrentSchemaVersion,
            capturedAtUtc,
            app,
            os,
            Environment.IsPrivilegedProcess,
            new SnapshotSources(display.Result, edid.Result, adapters.Result, smbios.Result, pcie.Result, storage.Result, steam.Result, power.Result));
    }

    private static async Task<SourceEntry<T>> Run<T>(string source, Func<T> collect, UserProfileRedactor profile)
        where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        var work = Task.Run(collect);

        try
        {
            var data = await work.WaitAsync(SourceTimeout).ConfigureAwait(false);
            return SourceEntry.Ok(source, stopwatch.ElapsedMilliseconds, data);
        }
        catch (TimeoutException)
        {
            // The work can't be cancelled (WMI, driver calls); it is abandoned and its result ignored.
            _ = work.ContinueWith(static task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return SourceEntry.Unavailable<T>(source, stopwatch.ElapsedMilliseconds, MissingReason.TimedOut, $"No answer within {SourceTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            var (reason, detail) = Describe(ex);
            return SourceEntry.Unavailable<T>(source, stopwatch.ElapsedMilliseconds, reason, profile.Redact(detail));
        }
    }

    /// <summary>Every failure becomes data: a reason and a detail naming what failed (architecture §10).</summary>
    private static (MissingReason Reason, string Detail) Describe(Exception ex) => ex switch
    {
        SourceUnavailableException unavailable => (unavailable.Reason, unavailable.Message),
        UnauthorizedAccessException or System.Security.SecurityException => (MissingReason.AccessDenied, $"{ex.GetType().Name}: {ex.Message}"),
        COMException com => (Win32Errors.ReasonForHResult(com.HResult), Win32Errors.Describe(com.TargetSite?.Name ?? "COM call", com.HResult)),
        ManagementException wmi => (wmi.ErrorCode switch
        {
            ManagementStatus.AccessDenied => MissingReason.AccessDenied,
            ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotSupported => MissingReason.NotSupported,
            ManagementStatus.Timedout => MissingReason.TimedOut,
            _ => MissingReason.SourceFailed,
        }, $"WMI {wmi.ErrorCode}"),
        IOException => (MissingReason.SourceFailed, $"{ex.GetType().Name}: {ex.Message}"),

        // Not an OS failure: a bug in the collector. Say so, and where, so it isn't read as "Windows didn't report".
        _ => (MissingReason.SourceFailed, $"Internal error in the collector ({ex.GetType().Name} in {ex.TargetSite?.DeclaringType?.Name}.{ex.TargetSite?.Name}): {ex.Message}"),
    };
}
