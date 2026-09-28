using System.Reflection;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;
using FpsUtility.Core.Support;
using FpsUtility.Platform.Windows;

namespace FpsUtility.Cli;

/// <summary>
/// <c>FpsUtility.Cli scan --json [--out &lt;path&gt;]</c> (architecture §4.4, "CLI surface in step 1").
/// Order: parse arguments; read the OS facts and refuse an unsupported platform before anything is
/// collected (D011); collect; serialise; write. The JSON goes to stdout or to the named file; every
/// message goes to stderr, so stdout carries nothing but the snapshot.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage:
          FpsUtility.Cli scan --json [--out <path>]

        Collects a raw machine snapshot (no diagnosis) and writes it as JSON to stdout,
        or to <path> with --out. Reads only; changes nothing on this PC.

        Exit codes:
          0  snapshot written (sources that could not be read are marked unavailable in it)
          1  the snapshot could not be produced or written
          2  invalid arguments
          3  unsupported platform: nothing was collected or written
        """;

    private static int Main(string[] args)
    {
        if (!TryParse(args, out var outPath, out var usageError))
        {
            if (usageError is not null)
            {
                Console.Error.WriteLine(usageError);
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine(Usage);
            return usageError is null ? ExitCode.Ok : ExitCode.Usage;
        }

        try
        {
            var os = OsInfoReader.Read();
            var decision = PlatformSupport.Evaluate(os);
            if (!decision.IsSupported)
            {
                Console.Error.WriteLine(decision.Message);
                return ExitCode.UnsupportedPlatform;
            }

            var snapshot = SnapshotCollector.Collect(os, ReadAppInfo());
            var json = SnapshotJson.Serialize(snapshot);

            if (outPath is null)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(json);
            }
            else
            {
                LocalStore.WriteUserExport(outPath, json);
            }

            ReportSources(snapshot, outPath);
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            // Sources never throw (their failures are data); this is a bug or an output failure.
            Console.Error.WriteLine($"scan failed: {ex.GetType().Name}: {ex.Message}");
            return ExitCode.Failed;
        }
    }

    /// <summary>Accepts exactly <c>scan --json</c> with an optional <c>--out &lt;path&gt;</c>, in any order after <c>scan</c>.</summary>
    private static bool TryParse(string[] args, out string? outPath, out string? error)
    {
        outPath = null;
        error = null;

        if (args is ["--help"] or ["-h"] or ["help"])
        {
            return false;
        }

        if (args.Length == 0)
        {
            error = "No command given.";
            return false;
        }

        if (args[0] != "scan")
        {
            error = $"Unknown command '{args[0]}'.";
            return false;
        }

        var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--out" when outPath is null && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    outPath = args[++i];
                    break;
                case "--out":
                    error = "--out needs one non-empty path.";
                    return false;
                default:
                    error = $"Unexpected argument '{args[i]}'.";
                    return false;
            }
        }

        if (!json)
        {
            error = "scan needs --json: in this version the only output is the raw JSON snapshot.";
            return false;
        }

        return true;
    }

    private static AppInfo ReadAppInfo()
    {
        // The SDK stamps "<version>+<commit>" when the build runs inside the git repository; the build
        // appends ".dirty" when the working tree had uncommitted changes (FpsUtility.Cli.csproj).
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? informational : informational[..plus];
        var commit = plus < 0 ? "" : informational[(plus + 1)..];

        Reading<string> source = commit switch
        {
            "" => Reading.Missing<string>(MissingReason.NotPresent, "The build was not stamped with a source commit"),
            _ when commit.EndsWith(".dirty", StringComparison.Ordinal) => Reading.Missing<string>(
                MissingReason.NotPresent,
                $"No single commit produced this build: it was built from uncommitted changes on top of {commit[..^".dirty".Length]}"),
            _ => Reading.Known(commit),
        };

        return new AppInfo(
            Name: "FpsUtility",
            Version: string.IsNullOrEmpty(version) ? "unknown" : version,
            Commit: source);
    }

    /// <summary>A one-line-per-source summary on stderr, so a person sees what was and wasn't read.</summary>
    private static void ReportSources(RawSnapshot snapshot, string? outPath)
    {
        var sources = snapshot.Sources;
        (string Name, SourceStatus Status, MissingReason? Reason, long Ms)[] entries =
        [
            ("display", sources.Display.Status, sources.Display.Reason, sources.Display.DurationMs),
            ("edid", sources.Edid.Status, sources.Edid.Reason, sources.Edid.DurationMs),
            ("adapters", sources.Adapters.Status, sources.Adapters.Reason, sources.Adapters.DurationMs),
            ("smbios", sources.Smbios.Status, sources.Smbios.Reason, sources.Smbios.DurationMs),
            ("pcie", sources.Pcie.Status, sources.Pcie.Reason, sources.Pcie.DurationMs),
            ("storage", sources.Storage.Status, sources.Storage.Reason, sources.Storage.DurationMs),
            ("steam", sources.Steam.Status, sources.Steam.Reason, sources.Steam.DurationMs),
            ("power", sources.Power.Status, sources.Power.Reason, sources.Power.DurationMs),
        ];

        Console.Error.WriteLine(outPath is null ? "Snapshot written to stdout." : $"Snapshot written to {outPath}.");
        foreach (var (name, status, reason, ms) in entries)
        {
            Console.Error.WriteLine(status == SourceStatus.Ok
                ? $"  {name,-9} ok           {ms,6} ms"
                : $"  {name,-9} unavailable  {ms,6} ms  ({reason})");
        }
    }

    private static class ExitCode
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int Usage = 2;

        /// <summary>The dedicated refusal code (D011): nothing was collected or written.</summary>
        public const int UnsupportedPlatform = 3;
    }
}
