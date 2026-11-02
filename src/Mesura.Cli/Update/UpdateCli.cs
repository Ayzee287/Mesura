using System.Globalization;
using Mesura.Core.Support;
using Mesura.Core.Update;
using Mesura.Updating;

namespace Mesura.Cli.Update;

/// <summary>
/// <c>mesura update</c> (0.3): <c>check</c> and <c>status</c> are read-only; <c>apply</c> is what the app starts, from a copy of
/// this program outside the program folder, to let the Windows Installer replace the installed version after the app has closed
/// (docs/updates.md). It checks the signature and hash of what it was given again before it runs anything.
/// </summary>
public static class UpdateCli
{
    public const string Usage = """
          mesura update check [--channel stable|preview] [--base <url>]
          mesura update status
          mesura update copies [remove --yes]
          mesura update stage --package <msi> --manifest <json> --sig <file> --pid <n> [--data <folder>] [--no-elevate 1]
          mesura update apply --package <msi> --manifest <json> --sig <file> --pid <n> --restart <Mesura.exe> --from <v> --to <v>
                              --log <file> --scope user|machine [--folder <install folder>] [--data <folder>] [--language en|ru] [--no-elevate 1]
        """;

    public const string Help = """
        update           stage is the elevated first half of a machine-wide update (it verifies the package and moves it into a folder only
                         administrators can change); check asks the update server for the newest version on the channel and says whether it is newer
                         (exit 0 up to date, 10 an update is available, 1 the server couldn't be reached or its answer was
                         refused). Nothing is downloaded or changed. status shows how Mesura is installed and how the last
                         update ended. apply is for the app: it runs the installer after the app has closed.
        """;

    public static int Run(string[] args)
    {
        return args switch
        {
            ["check", .. var rest] => Check(Options(rest)),
            ["status"] => Status(),
            ["copies"] => Copies(remove: false, confirmed: false),
            ["copies", "remove", "--yes"] => Copies(remove: true, confirmed: true),
            ["copies", "remove"] => Copies(remove: true, confirmed: false),
            ["stage", .. var rest] => Stage(Options(rest)),
            ["apply", .. var rest] => Apply(Options(rest)),
            _ => Usage_(),
        };
    }

    /// <summary>
    /// Lists the Windows Installer registrations of Mesura and what moving to the other kind of copy would do (the same decision the Settings page makes);
    /// <c>remove --yes</c> removes the other copy exactly as its button does (never the copy this program belongs to). Without <c>--yes</c> nothing is changed.
    /// </summary>
    private static int Copies(bool remove, bool confirmed)
    {
        var registrations = new WindowsInstallerCopies();
        var copies = registrations.Read();
        var self = Environment.ProcessPath ?? "";
        var running = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self)) ?? "", "Mesura.exe"); // cli\mesura.exe sits beside Mesura.exe's folder
        foreach (var c in copies)
        {
            Console.WriteLine($"{c.Scope,-8} {c.ProductCode} version {c.Version} in {c.Folder}");
        }

        if (copies.Count == 0)
        {
            Console.WriteLine("No Windows Installer registration of Mesura was found.");
        }

        var plan = CopyTransition.Plan(running, copies);
        Console.WriteLine(plan.Kind switch
        {
            TransitionKind.Offer => $"The other copy ({plan.Other!.Scope}, {plan.Other.ProductCode}) can be removed.",
            TransitionKind.Manual => $"The other copy ({plan.Other!.Scope}) must be removed by hand: {plan.Reason}.",
            _ => $"Nothing to move{(plan.Reason.Length > 0 ? " (" + plan.Reason + ")" : "")}.",
        });
        if (!remove)
        {
            return 0;
        }

        if (!confirmed)
        {
            Console.Error.WriteLine("Nothing was removed: add --yes to remove the other copy.");
            return 2;
        }

        var result = CopyTransition.Remove(plan, elevated: false, running, registrations, new MsiexecUninstaller());
        Console.WriteLine($"{result.Status}: {result.Detail}");
        return result.Status == RemovalStatus.Removed ? 0 : 1;
    }

    private static int Usage_()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        return map;
    }

    private static int Check(Dictionary<string, string> o)
    {
        var channel = o.GetValueOrDefault("channel") ?? "stable";
        var installed = VersionOfThisBuild();
        using var client = new UpdateClient();
        var result = client.CheckAsync(new UpdateRequest(installed, channel, new UpdateState(), UpdateKeys.Trusted, "x64", o.GetValueOrDefault("base"), InstallRegistration.Read()?.Language)).GetAwaiter().GetResult();
        if (!result.Reached || result.Check is null)
        {
            Console.Error.WriteLine($"The update server couldn't be used ({result.Failure}: {result.Detail}). The installed version {installed} keeps working.");
            return 1;
        }

        if (result.Check.Outcome == UpdateOutcome.UpToDate)
        {
            Console.WriteLine($"Mesura {installed} is up to date on the {channel} channel.");
            return 0;
        }

        var manifest = result.Check.Manifest!;
        Console.WriteLine($"Mesura {manifest.Version} is available on the {channel} channel (this is {installed}); {result.Check.Package!.Size / 1_000_000} MB.");
        Console.WriteLine(manifest.NotesFor("en"));
        return 10;
    }

    private static int Status()
    {
        var install = InstallRegistration.Read();
        Console.WriteLine(install is null
            ? "Mesura isn't installed by the installer (a portable copy). Updates for it run the installer."
            : $"Installed {install.Scope.ToString().ToLowerInvariant()}-wide in {install.Folder}, version {install.Version}.");
        var store = new UpdateStore(DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        var last = UpdateResult.TryRead(store.Read(UpdateStore.ResultFile));
        Console.WriteLine(last is null ? "No update has run on this PC." : $"The last update ({last.From} to {last.To}) {last.Status}{(last.Reason.Length > 0 ? ": " + last.Reason : "")} at {last.At:u}.");
        return 0;
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The elevated entry of a machine-wide update (docs/security.md). It runs only as the registered, installed command line (Program Files), takes
    /// what it needs from the registration rather than the arguments, verifies and moves the package into a protected folder, and starts the updater
    /// from there.
    /// </summary>
    private static int Stage(Dictionary<string, string> o)
    {
        string Need(string name) => o.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");
        StageRequest request;
        int pid;
        try
        {
            request = new StageRequest(Need("package"), Need("manifest"), Need("sig"));
            pid = int.Parse(Need("pid"), CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var install = InstallRegistration.Read();
        var self = Environment.ProcessPath is { } path ? Path.GetFullPath(path) : "";
        if (install is not { Scope: InstallScope.Machine } || !string.Equals(self, install.CommandLine, StringComparison.OrdinalIgnoreCase) || !IsAdministrator())
        {
            Console.Error.WriteLine("update stage runs only as administrator, from the command line of a machine-wide install.");
            return 3;
        }

        var data = o.GetValueOrDefault("data") ?? DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var running = VersionOfThisBuild();
        var outcome = UpdateStager.Stage(request, install, running, UpdateKeys.Trusted, DateTimeOffset.UtcNow, new ProgramDataProtectedFolders());
        if (outcome.Staged is not { } staged)
        {
            Console.Error.WriteLine("The update was not taken over: " + outcome.Failure);
            Say(data, new UpdateResult(UpdateResult.Failed, running, "", outcome.Failure ?? "stage", 0, "", DateTimeOffset.Now));
            Restart(install, pid, o);
            return 1;
        }

        string[] arguments =
        [
            "update", "apply",
            "--package", staged.Package, "--manifest", staged.Manifest, "--sig", staged.Signature,
            "--pid", pid.ToString(CultureInfo.InvariantCulture),
            "--restart", install.Executable, "--from", running, "--to", staged.ToVersion,
            "--log", staged.Log, "--scope", "machine", "--folder", install.Folder, "--data", data, "--language", install.Language,
            .. o.GetValueOrDefault("no-elevate") == "1" ? ["--no-elevate", "1"] : Array.Empty<string>(),
        ];
        if (InstallerLauncher.StartStaged(staged.Executable, arguments) is { } error)
        {
            Console.Error.WriteLine("The protected updater didn't start: " + error);
            Say(data, new UpdateResult(UpdateResult.Failed, running, staged.ToVersion, "installer-not-started", 0, "", DateTimeOffset.Now));
            Restart(install, pid, o);
            return 1;
        }

        return 0;
    }

    private static void Say(string data, UpdateResult result)
    {
        try
        {
            new UpdateStore(data).WriteText(UpdateStore.ResultFile, System.Text.Json.JsonSerializer.Serialize(result, UpdateResult.Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A courtesy to the next start.
        }
    }

    /// <summary>The app closed for the update; when nothing was installed it is started again (the installed version, untouched).</summary>
    private static void Restart(InstallInfo install, int appProcessId, Dictionary<string, string> o)
    {
        var launcher = new InstallerLauncher();
        launcher.WaitForExit(appProcessId, TimeSpan.FromSeconds(30));
        launcher.Restart(install.Executable, "--update-failed" + (o.GetValueOrDefault("no-elevate") == "1" ? " --no-elevate" : ""));
    }

    private static int Apply(Dictionary<string, string> o)
    {
        // An elevated updater is only as trustworthy as the folder it runs from: refuse one that users can change (docs/security.md).
        if (o.GetValueOrDefault("scope") == "machine" && IsAdministrator() && Environment.ProcessPath is { } own
            && ProgramFolderTrust.Why(Path.GetDirectoryName(Path.GetFullPath(own))!) is { } exposed)
        {
            Console.Error.WriteLine($"The updater won't run with administrator rights from {own}: {exposed}.");
            return 3;
        }

        string Need(string name) => o.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");
        ApplyRequest request;
        try
        {
            request = new ApplyRequest(
                Need("package"), Need("manifest"), Need("sig"), int.Parse(Need("pid"), CultureInfo.InvariantCulture), Need("restart"), Need("from"), Need("to"), Need("log"),
                Need("scope") == "machine" ? InstallScope.Machine : InstallScope.User, UpdateKeys.Trusted, o.GetValueOrDefault("folder"), o.GetValueOrDefault("no-elevate") == "1", o.GetValueOrDefault("language"));
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var data = o.GetValueOrDefault("data") ?? DataFolder.Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var result = new UpdateApplier(new UpdateStore(data), new InstallerLauncher()).Apply(request);
        return result.Status == UpdateResult.Succeeded ? 0 : 1;
    }

    /// <summary>The version this program was built as (the command line ships beside the app, always the same build).</summary>
    private static string VersionOfThisBuild()
    {
        var informational = typeof(UpdateCli).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
