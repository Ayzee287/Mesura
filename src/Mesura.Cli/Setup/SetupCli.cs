using Mesura.Core.Apps;
using Mesura.Core.Licensing;
using Mesura.Core.Nvidia;
using Mesura.Core.Setup;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Apps;
using Mesura.Setup.Windows.Nvidia;
using Mesura.Setup.Windows.Workflow;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura setup status</c> (D031): what the app's Setup and Activation pages read, as text and read-only — the Visual
/// C++ runtimes installed, the NVIDIA GPU with its driver and the newest one NVIDIA offers, the NVIDIA tools, the Windows
/// and Office licences (no key, ever), and an unfinished setup. The setup itself runs from the app, where its one
/// confirmation and the NVIDIA step's checklist are.
/// </summary>
public static class SetupCli
{
    public const string Usage = """
          mesura setup status [--offline]
            What a setup would work with on this PC, read-only: Visual C++ runtimes, the NVIDIA GPU and driver (with the
            newest driver from NVIDIA's lookup service unless --offline), NVCleanstall and NVIDIA Profile Inspector, the
            Windows and Office licences, and an unfinished setup. The setup runs from the app (Setup).
        """;

    public static int Run(string[] args)
    {
        if (args is not ["status", .. var rest] || rest.Any(a => a != "--offline"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var offline = rest.Contains("--offline");
        Runtimes();
        Nvidia(offline);
        Activation();
        Pending();
        return 0;
    }

    private static void Runtimes()
    {
        Console.WriteLine("Visual C++ runtimes (Microsoft's packages through winget):");
        var installed = VcRuntimeDetector.Detect();
        if (installed is null)
        {
            Console.WriteLine("  couldn't read the registry.");
            return;
        }

        foreach (var runtime in VcRuntimes.All)
        {
            var found = installed.FirstOrDefault(i => i.Id == runtime.Id);
            var state = found is null ? "not installed" : $"installed {found.Version}".TrimEnd();
            var support = runtime.Supported ? "supported" : "unsupported, for older games";
            Console.WriteLine($"  {runtime.Package.Name,-28} {state,-24} ({support}{(runtime.Default ? "" : "; not ticked by default")})");
        }
    }

    private static void Nvidia(bool offline)
    {
        Console.WriteLine();
        Console.WriteLine("NVIDIA:");
        var config = NvidiaDriverConfig.Default;
        Console.WriteLine("  Default NVIDIA configuration (the app's saved one may differ):");
        Console.WriteLine($"    components kept: {string.Join(", ", config.Components.Select(id => NvidiaComponents.ById(id)!.Label))}");
        Console.WriteLine($"    tweaks ticked: {string.Join("; ", config.Tweaks.Select(id => NvidiaTweaks.ById(id)!.Label))}");
        Console.WriteLine($"    Interrupt Policy: {config.InterruptPolicy}; Interrupt Priority: {config.InterruptPriority}");
        byte[]? original = null;
        if (config.Profile is { } reference && NvidiaProfiles.ById(reference.Id) is { } profile)
        {
            (original, var error) = NvidiaProfiles.Load(reference);
            Console.WriteLine($"    profile imported after the driver: {profile.Name} ({profile.Id}, SHA-256 {profile.Sha256[..16]}...)"
                + (error is null ? ", file in this build checked" : $": {error}"));
        }
        else
        {
            Console.WriteLine("    profile imported after the driver: none set");
        }

        var gpus = SystemSummaryReader.ReadNvidia();
        if (original is not null)
        {
            // As the import would fit it here.
            foreach (var setting in MesuraNvidiaProfile.Settings)
            {
                Console.WriteLine($"      {setting.Name} = {setting.ValueMeaning}{(setting.DesktopOnly ? " (desktops only)" : "")}");
            }

            var fit = NvidiaProfilePortability.Adapt(original, new SystemStateReader().HasBattery);
            Console.WriteLine(fit.Adjusted
                ? "    fitted to this PC before the import (the original file stays unchanged):"
                : "    imported unchanged on this PC");
            foreach (var adjustment in fit.Adjustments)
            {
                Console.WriteLine($"      {adjustment.Setting}: {adjustment.OriginalShort} in the file -> {adjustment.Imported ?? "left out"}. {adjustment.Reason}");
            }
        }

        if (gpus is null)
        {
            Console.WriteLine("  the graphics adapters couldn't be read.");
            return;
        }

        if (gpus.Count == 0)
        {
            Console.WriteLine("  no NVIDIA graphics card: the NVIDIA steps don't apply here.");
            return;
        }

        var build = Environment.OSVersion.Version.Build;
        foreach (var gpu in gpus)
        {
            Console.WriteLine($"  {gpu.Name} (device {gpu.DeviceHex}), driver {gpu.Release ?? "unread"} ({gpu.DriverVersion})");
            if (!offline)
            {
                var lookup = NvidiaDriverLookup.LatestAsync(gpu, build).GetAwaiter().GetResult();
                Console.WriteLine(lookup.Release is { } release
                    ? $"  newest WHQL Game Ready driver: {release.Version} ({release.ReleaseDate}){(NvidiaDriver.IsNewer(release.Version, gpu.Release) ? ", newer than installed" : ", installed")}"
                    : $"  newest driver: {lookup.Error}");
            }
        }

        var tools = new NvidiaTools();
        Console.WriteLine($"  NVCleanstall: {tools.FindNvCleanstall() ?? "not installed (a setup installs it with winget)"}");
        Console.WriteLine(tools.FindProfileInspector() is { } inspector
            ? $"  NVIDIA Profile Inspector: {inspector.Version}{(inspector.CanMerge ? "" : $" (older than {ProfileInspector.MergeImport}: can't merge-import)")} at {inspector.Path}"
            : "  NVIDIA Profile Inspector: not installed (a setup installs it with winget)");
    }

    private static void Activation()
    {
        Console.WriteLine();
        Console.WriteLine("Licences (as Windows reports them; no key is read):");
        var status = LicensingReader.Read();
        if (status.Error is { } error)
        {
            Console.WriteLine($"  couldn't be read: {error}");
            return;
        }

        foreach (var license in status.Windows.Concat(status.Office))
        {
            var grace = license.GraceDays is { } days ? $", {days} day(s) {(license.State == LicenseState.Licensed ? "until renewal" : "left")}" : "";
            var host = license.KmsHost is { Length: > 0 } kms ? $", KMS host {kms}" : "";
            Console.WriteLine($"  {license.Product}: {license.Name}; {license.State}; {license.Channel}{grace}{host}");
        }

        if (status.FirmwareLicense is { } firmware)
        {
            Console.WriteLine($"  Licence in the firmware: {firmware}");
        }

        if (status.OfficeInstall is { } office)
        {
            Console.WriteLine($"  Office (Click-to-Run): {office.ProductReleaseIds} {office.Version}");
        }
    }

    private static void Pending()
    {
        Console.WriteLine();
        var pending = SetupWorkflowRunner.Pending(new SetupStore());
        if (pending is null)
        {
            Console.WriteLine("No unfinished setup.");
            return;
        }

        Console.WriteLine($"Unfinished setup {pending.RunId}:");
        foreach (var step in pending.Steps)
        {
            Console.WriteLine($"  {RunFollowUp.SetupStepName(step.Kind),-24} {step.State}{(step.Detail.Length > 0 ? " — " + step.Detail : "")}");
        }
    }
}
