using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FpsUtility.Core.Checks;
using FpsUtility.Core.Checks.Serialization;
using FpsUtility.Core.Facts.Serialization;
using FpsUtility.Core.Normalize;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;
using FpsUtility.Core.Support;
using FpsUtility.Platform.Windows;

namespace FpsUtility.Cli;

/// <summary>
/// The CLI surface of architecture §4.4:
/// <list type="bullet">
/// <item><c>scan --json [--out &lt;path&gt;]</c> writes a raw snapshot to stdout or a file;</item>
/// <item><c>capture-fixture --id PC-NNN --out &lt;folder&gt;</c> writes a pseudonymised, leak-checked
/// fixture of a Track A PC into a folder outside git;</item>
/// <item><c>normalize --in &lt;snapshot&gt; [--out &lt;path&gt;]</c> reads a raw snapshot file and writes its
/// normalised facts (Milestone 2): the validation path from raw observation to fact, with no diagnosis.</item>
/// <item><c>check --in &lt;snapshot&gt; [--out &lt;path&gt;]</c> reads a raw snapshot file, normalises it and writes the
/// results of the implemented checks (Milestone 3A: checks 1 and 2), with no explanations.</item>
/// </list>
/// All parse their arguments, then read the OS facts and refuse an unsupported platform (D011) before
/// anything is collected or read. Every message goes to stderr, so stdout is only the JSON.
/// </summary>
internal static partial class Program
{
    private const string Usage = """
        Usage:
          FpsUtility.Cli scan --json [--out <path>]
          FpsUtility.Cli capture-fixture --id <PC-NNN | PCB-NNN> --out <folder>
          FpsUtility.Cli normalize --in <snapshot.json> [--out <path>]
          FpsUtility.Cli check --in <snapshot.json> [--out <path>]

        scan             Collects a raw machine snapshot (no diagnosis) and writes it as JSON to
                         stdout, or to <path> with --out.
        capture-fixture  Collects a snapshot, replaces its opaque device ids with stand-ins, checks it
                         holds none of this PC's identifiers, and writes it into an existing <folder>
                         outside any git repository, named <ID>-<UTC time>.json.
        normalize        Reads a raw snapshot (from scan or capture-fixture) and writes its normalised
                         facts as JSON to stdout, or to <path> with --out. No checks, no diagnosis.
        check            Reads a raw snapshot, normalises it and writes the results of the checks
                         implemented so far (1 and 2) as JSON to stdout, or to <path> with --out.
                         Verdicts and evidence only: no explanations, no recommendations.

        All read only and change nothing on this PC.

        Exit codes:
          0  written (sources that could not be read are marked unavailable in it; for check,
             whatever the verdicts are)
          1  the snapshot or facts could not be produced, read or written
          2  invalid arguments
          3  unsupported platform: nothing was collected or written
          4  capture-fixture: the privacy check found an identifier; nothing was written
        """;

    private static readonly UserProfileRedactor Profile = new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static int Main(string[] args)
    {
        var command = Parse(args, out var usageError);
        if (command is null)
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

            return command switch
            {
                ScanCommand scan => Scan(os, scan),
                CaptureFixtureCommand capture => CaptureFixture(os, capture),
                NormalizeCommand normalize => Normalize(normalize),
                CheckCommand check => Check(check),
                _ => throw new InvalidOperationException("Unknown command."),
            };
        }
        catch (Exception ex)
        {
            // Sources never throw (their failures are data); this is a bug or an output failure.
            Console.Error.WriteLine(Profile.Redact($"{command.Name} failed: {ex.GetType().Name}: {ex.Message}"));
            return ExitCode.Failed;
        }
    }

    private static int Scan(OsInfo os, ScanCommand command)
    {
        var snapshot = SnapshotCollector.Collect(os, ReadAppInfo());
        Write(SnapshotJson.Serialize(snapshot), command.OutPath, "Snapshot");
        ReportSources(snapshot);
        return ExitCode.Ok;
    }

    /// <summary>
    /// Architecture §4.4 <c>capture-fixture</c>: collect, pseudonymise the opaque ids, prove no local
    /// identifier is in it, then write it, and show what it contains so the owner can review it.
    /// </summary>
    private static int CaptureFixture(OsInfo os, CaptureFixtureCommand command)
    {
        var (snapshot, idsReplaced) = FixturePseudonymizer.Pseudonymize(SnapshotCollector.Collect(os, ReadAppInfo()));
        var json = SnapshotJson.Serialize(snapshot);

        var (identifiers, unread) = LocalIdentifiers.Read();
        var leaks = IdentifierLeakCheck.FindLeaks(json, identifiers);
        if (leaks.Count > 0)
        {
            Console.Error.WriteLine($"Privacy check failed: the fixture contains this PC's {string.Join(", ", leaks)}. Nothing was written.");
            return ExitCode.PrivacyCheckFailed;
        }

        var fileName = string.Create(CultureInfo.InvariantCulture, $"{command.Id}-{snapshot.CapturedAtUtc:yyyyMMdd'T'HHmmss'Z'}.json");
        var path = Path.Combine(command.Folder, fileName);
        LocalStore.WriteUserExport(path, json);

        Console.Error.WriteLine(Profile.Redact($"Fixture {command.Id} written to {path}."));
        ReportSources(snapshot);
        ReportContents(snapshot, idsReplaced);

        var checkedCategories = identifiers.Where(IdentifierLeakCheck.IsCheckable).Select(identifier => identifier.Category).Distinct().Count();
        Console.Error.WriteLine($"Privacy check passed: none of this PC's identifiers were found ({checkedCategories} kinds checked).");
        if (unread.Count > 0)
        {
            Console.Error.WriteLine($"  Could not be read, so not checked: {string.Join(", ", unread.Distinct())}.");
        }

        Console.Error.WriteLine("Review the file before sharing it. It keeps hardware facts, and Steam game names if Steam is installed.");
        return ExitCode.Ok;
    }

    /// <summary>
    /// Milestone 2's validation path: a raw snapshot file in, its normalised facts out. Reading the file is strict
    /// (a malformed or older-schema snapshot is refused with exit 1); normalising never touches the PC.
    /// </summary>
    private static int Normalize(NormalizeCommand command)
    {
        if (ReadSnapshot(command.InPath) is not { } raw)
        {
            return ExitCode.Failed;
        }

        Write(FactsJson.Serialize(Normalizer.Normalize(raw)), command.OutPath, "Facts");
        return ExitCode.Ok;
    }

    /// <summary>
    /// Milestone 3A's validation path: a raw snapshot file in, the implemented checks' results out, under the product's
    /// fixture gate (nothing is Confirmed until an owner-labelled fixture unlocks it). The exit code never depends on a
    /// verdict, and a Couldn't check is never counted with the problems.
    /// </summary>
    private static int Check(CheckCommand command)
    {
        if (ReadSnapshot(command.InPath) is not { } raw)
        {
            return ExitCode.Failed;
        }

        var results = CheckRunner.Run(Normalizer.Normalize(raw), FixtureGate.Validated);
        Write(CheckResultsJson.Serialize(results), command.OutPath, "Check results");

        foreach (var result in results.Results)
        {
            var certainty = result.Certainty is { } sure ? $" {sure}" : "";
            Console.Error.WriteLine($"  {result.Check,-27} {result.Subject,-10} {result.Verdict}{certainty}");
        }

        foreach (var entry in results.Coverage.Where(entry => entry.Status != CoverageStatus.Ran))
        {
            Console.Error.WriteLine($"  {entry.Check,-27} {entry.Status}");
        }

        return ExitCode.Ok;
    }

    /// <summary>A raw snapshot file, read strictly: an older schema or a malformed file is reported, and gives <c>null</c>.</summary>
    private static RawSnapshot? ReadSnapshot(string path)
    {
        try
        {
            return SnapshotJson.Deserialize(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is SnapshotFormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(Profile.Redact($"The raw snapshot could not be read: {ex.Message}"));
            return null;
        }
    }

    /// <summary>JSON to stdout, or to the user's chosen file through <see cref="LocalStore"/> (§2.3).</summary>
    private static void Write(byte[] json, string? outPath, string what)
    {
        if (outPath is null)
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(json);
            Console.Error.WriteLine($"{what} written to stdout.");
        }
        else
        {
            LocalStore.WriteUserExport(outPath, json);
            Console.Error.WriteLine(Profile.Redact($"{what} written to {outPath}."));
        }
    }

    private static Command? Parse(string[] args, out string? error)
    {
        error = null;

        if (args is ["--help"] or ["-h"] or ["help"])
        {
            return null;
        }

        if (args.Length == 0)
        {
            error = "No command given.";
            return null;
        }

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (options.ContainsKey(name))
            {
                error = $"'{name}' is given twice.";
                return null;
            }

            switch (name)
            {
                case "--json":
                    options[name] = null;
                    break;
                case "--out" or "--id" or "--in" when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    options[name] = args[++i];
                    break;
                case "--out" or "--id" or "--in":
                    error = $"{name} needs one non-empty value.";
                    return null;
                default:
                    error = $"Unexpected argument '{name}'.";
                    return null;
            }
        }

        return args[0] switch
        {
            "scan" => ParseScan(options, out error),
            "capture-fixture" => ParseCaptureFixture(options, out error),
            "normalize" => ParseNormalize(options, out error),
            "check" => ParseCheck(options, out error),
            _ => Fail($"Unknown command '{args[0]}'.", out error),
        };
    }

    private static ScanCommand? ParseScan(Dictionary<string, string?> options, out string? error)
    {
        error = null;
        if (!options.ContainsKey("--json"))
        {
            return Fail<ScanCommand>("scan needs --json: in this version the only output is the raw JSON snapshot.", out error);
        }

        if (options.ContainsKey("--id") || options.ContainsKey("--in"))
        {
            return Fail<ScanCommand>("scan takes no --id or --in.", out error);
        }

        return new ScanCommand(options.GetValueOrDefault("--out"));
    }

    private static CaptureFixtureCommand? ParseCaptureFixture(Dictionary<string, string?> options, out string? error)
    {
        error = null;
        var id = options.GetValueOrDefault("--id");
        var folder = options.GetValueOrDefault("--out");

        if (options.ContainsKey("--json") || options.ContainsKey("--in"))
        {
            return Fail<CaptureFixtureCommand>("capture-fixture always writes JSON and collects its own snapshot; it takes no --json or --in.", out error);
        }

        if (id is null || !FixtureId().IsMatch(id))
        {
            return Fail<CaptureFixtureCommand>("capture-fixture needs --id PC-NNN (Track A) or PCB-NNN (Track B), for example PC-001.", out error);
        }

        if (folder is null || !Directory.Exists(folder))
        {
            return Fail<CaptureFixtureCommand>("capture-fixture needs --out with an existing folder.", out error);
        }

        var full = Path.GetFullPath(folder);
        if (InsideGitRepository(full))
        {
            return Fail<CaptureFixtureCommand>("The fixture folder is inside a git repository. Fixtures stay outside git: use a folder under research\\validation-evidence\\.", out error);
        }

        return new CaptureFixtureCommand(id, full);
    }

    private static NormalizeCommand? ParseNormalize(Dictionary<string, string?> options, out string? error)
    {
        error = null;
        if (options.ContainsKey("--json") || options.ContainsKey("--id"))
        {
            return Fail<NormalizeCommand>("normalize always writes JSON and takes no --id.", out error);
        }

        return options.GetValueOrDefault("--in") is { } input
            ? new NormalizeCommand(input, options.GetValueOrDefault("--out"))
            : Fail<NormalizeCommand>("normalize needs --in with a raw snapshot file.", out error);
    }

    private static CheckCommand? ParseCheck(Dictionary<string, string?> options, out string? error)
    {
        error = null;
        if (options.ContainsKey("--json") || options.ContainsKey("--id"))
        {
            return Fail<CheckCommand>("check always writes JSON and takes no --id.", out error);
        }

        return options.GetValueOrDefault("--in") is { } input
            ? new CheckCommand(input, options.GetValueOrDefault("--out"))
            : Fail<CheckCommand>("check needs --in with a raw snapshot file.", out error);
    }

    /// <summary>A folder is inside a repository when it or any parent holds a <c>.git</c> folder or file.</summary>
    private static bool InsideGitRepository(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return true;
            }
        }

        return false;
    }

    private static Command? Fail(string message, out string? error) => Fail<Command>(message, out error);

    private static T? Fail<T>(string message, out string? error)
        where T : class
    {
        error = message;
        return null;
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
    private static void ReportSources(RawSnapshot snapshot)
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

        foreach (var (name, status, reason, ms) in entries)
        {
            Console.Error.WriteLine(status == SourceStatus.Ok
                ? $"  {name,-9} ok           {ms,6} ms"
                : $"  {name,-9} unavailable  {ms,6} ms  ({reason})");
        }
    }

    /// <summary>What the fixture holds, in counts only, so the owner can review it before storing it (§4.4).</summary>
    private static void ReportContents(RawSnapshot snapshot, int idsReplaced)
    {
        var sources = snapshot.Sources;
        var steamManifests = sources.Steam.Data?.Libraries.Value?.Sum(library => library.Manifests.Value?.Count ?? 0) ?? 0;

        Console.Error.WriteLine("Contents:");
        Console.Error.WriteLine($"  display outputs {sources.Display.Data?.Outputs.Count ?? 0}, monitors with EDID {sources.Edid.Data?.Monitors.Count(monitor => monitor.Edid.IsKnown) ?? 0}, graphics adapters {sources.Adapters.Data?.Adapters.Count ?? 0}");
        Console.Error.WriteLine($"  PCI devices {sources.Pcie.Data?.Devices.Count ?? 0}, physical disks {sources.Storage.Data?.PhysicalDisks.Count ?? 0}, OS disks {sources.Storage.Data?.Disks.Count ?? 0}, partitions {sources.Storage.Data?.Partitions.Count ?? 0}, volumes {sources.Storage.Data?.Volumes.Count ?? 0}");
        Console.Error.WriteLine($"  SMBIOS fields scrubbed {sources.Smbios.Data?.FieldsScrubbed ?? 0}, EDID fields scrubbed {sources.Edid.Data?.Monitors.Sum(monitor => monitor.Edid.Value?.FieldsScrubbed ?? 0) ?? 0}, opaque ids replaced {idsReplaced}, Steam app manifests {steamManifests}");
    }

    [GeneratedRegex(@"^PCB?-\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex FixtureId();

    private abstract record Command(string Name);

    private sealed record ScanCommand(string? OutPath) : Command("scan");

    private sealed record CaptureFixtureCommand(string Id, string Folder) : Command("capture-fixture");

    private sealed record NormalizeCommand(string InPath, string? OutPath) : Command("normalize");

    private sealed record CheckCommand(string InPath, string? OutPath) : Command("check");

    private static class ExitCode
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int Usage = 2;

        /// <summary>The dedicated refusal code (D011): nothing was collected or written.</summary>
        public const int UnsupportedPlatform = 3;

        /// <summary>capture-fixture found one of this PC's identifiers in the fixture; nothing was written.</summary>
        public const int PrivacyCheckFailed = 4;
    }
}
