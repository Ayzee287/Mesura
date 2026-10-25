using System.Globalization;
using Mesura.Cli.Setup;
using Mesura.Core.Config;
using Mesura.Core.Setup;
using Mesura.Platform.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Config;

namespace Mesura.Cli.Config;

/// <summary>
/// The <c>config</c> verbs: list the supported apps and their files, export this machine's allowlisted settings to a
/// portable bundle (read-only), and import a bundle (a dry run by default; <c>--apply</c> writes, and only while the
/// target app is closed). Export and list change nothing; import writes only with <c>--apply</c>.
/// </summary>
internal static class ConfigCli
{
    public const string Usage = """
          mesura config list
          mesura config export --out <bundle.json>
          mesura config import --in <bundle.json> [--apply]
        """;

    public const string Help = """
        config list      Lists the supported applications, the config files found for each, and how many
                         transferable settings are known. Read-only.
        config export    Reads each app's allowlisted settings into a portable bundle at --out. No
                         credentials, tokens, account ids or file paths are included. Read-only.
        config import    Reads a bundle from --in and shows what it would apply (a dry run). With --apply
                         it writes the allowlisted keys into each app's config, preserving every other key.
                         An app's settings are written only while that app is closed; Steam account settings
                         only once that account's config exists.
        """;

    // Exit codes mirror Program.ExitCode (0 ok, 1 failed, 2 usage); kept local to stay decoupled.
    private const int Ok = 0;
    private const int Failed = 1;
    private const int UsageCode = 2;

    private sealed record Request(string Verb, string? Path, bool Apply);

    public static int Run(string[] args)
    {
        var request = Parse(args, out var error);
        if (request is null)
        {
            Console.Error.WriteLine(error ?? Usage);
            return error is null ? Ok : UsageCode;
        }

        return request.Verb switch
        {
            "list" => List(),
            "export" => Export(request.Path!),
            "import" => Import(request.Path!, request.Apply),
            _ => UsageCode,
        };
    }

    private static Request? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            error = Usage;
            return null;
        }

        var verb = args[0];
        string? path = null;
        var apply = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "--in" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    path = args[++i];
                    break;
                case "--apply" when verb == "import":
                    apply = true;
                    break;
                default:
                    error = $"config: unexpected argument '{args[i]}'.";
                    return null;
            }
        }

        switch (verb)
        {
            case "list":
                return new Request(verb, null, false);
            case "export" when path is null:
                error = "config export needs --out <bundle.json>.";
                return null;
            case "import" when path is null:
                error = "config import needs --in <bundle.json>.";
                return null;
            case "export" or "import":
                return new Request(verb, path, apply);
            default:
                error = $"Unknown config verb '{verb}'. Use list, export or import.";
                return null;
        }
    }

    private static int List()
    {
        foreach (var port in ConfigCatalog.All)
        {
            Console.WriteLine($"{port.AppName}  ({port.Settings.Count} settings, id '{port.Id}')");
            foreach (var file in ConfigLocator.Locate(port))
            {
                Console.WriteLine($"    [{(file.Exists ? "present" : "absent ")}] {file.SourceId}: {file.Path}");
            }
        }

        return Ok;
    }

    /// <summary>
    /// App settings live in this process account's profile: when that isn't the signed-in user (a standard user
    /// elevated with another administrator's password), export and import refuse rather than read or write that account's.
    /// </summary>
    private static bool OtherAccount()
    {
        var reader = new Mesura.Platform.Windows.Windows.SystemStateReader();
        var user = reader.User;
        if (ProcessUser.Is(user, reader.IsElevated))
        {
            return false;
        }

        Console.Error.WriteLine($"Mesura runs as {ProcessUser.Account}, not as the signed-in user ({(user.Resolved ? user.Account : "unknown")}),"
            + " so the app settings here would be that account's. Run it as the signed-in user (without elevation is fine) to export or import app settings.");
        return true;
    }

    private static int Export(string outPath)
    {
        if (OtherAccount())
        {
            return Failed;
        }

        var result = ConfigService.Export(ConfigCatalog.All, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        LocalStore.WriteUserExport(outPath, System.Text.Encoding.UTF8.GetBytes(result.BundleJson));

        Console.Error.WriteLine($"Exported to {outPath}. What it contains:");
        foreach (var app in result.Apps)
        {
            var captured = app.Bundle.Values.Count;
            Console.Error.WriteLine($"  {app.Port.AppName}: {captured} setting(s) captured.");
            foreach (var item in app.Bundle.Preview)
            {
                var mark = item.Status switch
                {
                    CaptureStatus.Captured => "captured",
                    CaptureStatus.NotPresent => "not set  ",
                    _ => "excluded ",
                };
                Console.Error.WriteLine($"      [{mark}] {item.DisplayName}");
            }
        }

        return Ok;
    }

    private static int Import(string inPath, bool apply)
    {
        if (OtherAccount())
        {
            return Failed;
        }

        ConfigBundleFile bundle;
        try
        {
            bundle = ConfigBundleFile.Load(File.ReadAllText(inPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"Could not read the bundle: {e.Message}");
            return Failed;
        }

        if (!apply)
        {
            Console.WriteLine("Preview (nothing written):");
            var preview = ConfigService.Import(bundle, ConfigCatalog.All, store: null, runId: null);
            SetupOutput.WriteConfig(preview, preview: true);
            Console.WriteLine(preview.Any(o => o.Status == ConfigImportStatus.Applied)
                ? "Re-run with --apply to write these changes (close the apps first)."
                : "Nothing to change.");
            return Ok;
        }

        var started = DateTimeOffset.UtcNow;
        var store = new SetupStore();
        var id = SetupRun.NewId(started, "config-import");
        var outcomes = ConfigService.Import(bundle, ConfigCatalog.All, store, id);
        var run = SetupRun.Create(id, "config-import", null, SetupRun.Stamp(started), SetupRun.Stamp(DateTimeOffset.UtcNow), [], outcomes);
        if (outcomes.Any(o => o.Written.Count > 0 || o.Status != ConfigImportStatus.Unchanged))
        {
            store.WriteRunRecord(id, run.ToJson());
            Console.Error.WriteLine($"Recorded as history run {id}.");
        }

        Console.WriteLine("Applied:");
        SetupOutput.WriteConfig(outcomes, preview: false);
        SetupOutput.WriteFollowUps(run);
        return run.Ok ? Ok : Failed;
    }
}
