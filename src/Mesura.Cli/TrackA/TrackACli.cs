using System.Globalization;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.TrackA;

namespace Mesura.Cli.TrackA;

/// <summary>
/// <c>mesura track-a …</c>: the Track A capture kit's commands (architecture §4.7). They orchestrate the
/// existing pipeline (collection, the pseudonymiser, the normaliser, the checks, the leak check) through
/// <see cref="CapturePipeline"/> and <see cref="AcceptanceGate"/>; everything they decide is in Core, and this layer only
/// talks to the operator and reads and writes the files the operator named. Nothing is sent anywhere.
/// </summary>
internal static class TrackACli
{
    public const string Usage = """
          mesura track-a capture --out <folder> [--cohort PC-NNN] [--scenario <id>] [--delay <seconds>] [--keep-private] [--no-repeat-scan]
          mesura track-a verify --session <session folder>
          mesura track-a promote --session <session folder> --corpus <track-a folder>
          mesura track-a index --corpus <track-a folder>
          mesura track-a coverage --corpus <track-a folder>
        """;

    public const string Help = """
        track-a capture   The Track A capture kit (internal validation on known PCs). Scans this PC, scrubs,
                          normalises and checks the scan, asks only what the software can't know, verifies the
                          whole package against this PC's own identifiers, and writes it under
                          <folder>\PC-NNN\<session>\shareable. Asks for the cohort id and scenario if not given.
                          Scenarios: baseline, game-load, display-high-refresh, clone, motherboard-display,
                          pci-e-idle-load, steam-storage. --keep-private also keeps the unscrubbed scan in private\.
        track-a verify    Checks a captured session (integrity, format, privacy audit, replay) without accepting it.
        track-a promote   The acceptance gate: verifies an incoming session, asks for the manual review, and copies an
                          accepted session into <track-a folder>\cohort, then rebuilds cohort-index.json.
        track-a index     Rebuilds <track-a folder>\cohort-index.json from the accepted sessions and prints coverage.
        track-a coverage  Prints the coverage of the accepted sessions (an inventory, not a score).
        """;

    /// <summary>track-a: the acceptance gate or the verification refused the session; nothing was accepted.</summary>
    public const int GateRefused = 5;

    /// <summary>track-a capture: a privacy-verified package was written, but it can't be accepted as it is (see its manifest).</summary>
    public const int NotReady = 6;

    public abstract record Request;

    public sealed record CaptureRequestArgs(string Out, string? Cohort, string? Scenario, int? DelaySeconds, bool KeepPrivate, bool RepeatScan) : Request;

    public sealed record VerifyRequest(string Session) : Request;

    public sealed record PromoteRequest(string Session, string Corpus) : Request;

    public sealed record IndexRequest(string Corpus, bool Write) : Request;

    public static Request? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0)
        {
            error = "track-a needs a command: capture, verify, promote, index or coverage.";
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (values.ContainsKey(name) || flags.Contains(name))
            {
                error = $"'{name}' is given twice.";
                return null;
            }

            switch (name)
            {
                case "--keep-private" or "--no-repeat-scan":
                    flags.Add(name);
                    break;
                case "--out" or "--cohort" or "--scenario" or "--delay" or "--session" or "--corpus"
                    when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    values[name] = args[++i];
                    break;
                case "--out" or "--cohort" or "--scenario" or "--delay" or "--session" or "--corpus":
                    error = $"{name} needs one non-empty value.";
                    return null;
                default:
                    error = $"Unexpected argument '{name}'.";
                    return null;
            }
        }

        string[] Allowed(string verb) => verb switch
        {
            "capture" => ["--out", "--cohort", "--scenario", "--delay", "--keep-private", "--no-repeat-scan"],
            "verify" => ["--session"],
            "promote" => ["--session", "--corpus"],
            _ => ["--corpus"],
        };

        var verb = args[0];
        if (verb is not ("capture" or "verify" or "promote" or "index" or "coverage"))
        {
            error = $"Unknown track-a command '{verb}'.";
            return null;
        }

        if (values.Keys.Concat(flags).FirstOrDefault(option => !Allowed(verb).Contains(option)) is { } stray)
        {
            error = $"track-a {verb} takes no {stray}.";
            return null;
        }

        switch (verb)
        {
            case "capture":
                if (!values.TryGetValue("--out", out var output))
                {
                    error = "track-a capture needs --out with the folder captures go into (outside any git repository).";
                    return null;
                }

                if (values.TryGetValue("--cohort", out var cohort) && !CohortId.IsValid(cohort))
                {
                    error = "--cohort takes a Track A cohort id, PC-NNN (for example PC-001).";
                    return null;
                }

                if (values.TryGetValue("--scenario", out var scenario) && ScenarioCatalog.Find(scenario) is null)
                {
                    error = $"--scenario takes one of: {string.Join(", ", ScenarioCatalog.All.Select(item => item.Id))}.";
                    return null;
                }

                int? delay = null;
                if (values.TryGetValue("--delay", out var delayText))
                {
                    if (!int.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 300)
                    {
                        error = "--delay takes a whole number of seconds from 0 to 300.";
                        return null;
                    }

                    delay = seconds;
                }

                return new CaptureRequestArgs(output, cohort, scenario, delay, flags.Contains("--keep-private"), !flags.Contains("--no-repeat-scan"));
            case "verify":
                return values.TryGetValue("--session", out var session)
                    ? new VerifyRequest(session)
                    : Fail("track-a verify needs --session with a session folder (the one holding shareable\\).", out error);
            case "promote":
                return values.TryGetValue("--session", out var incoming) && values.TryGetValue("--corpus", out var corpus)
                    ? new PromoteRequest(incoming, corpus)
                    : Fail("track-a promote needs --session (the incoming session folder) and --corpus (the repository's track-a folder).", out error);
            default:
                return values.TryGetValue("--corpus", out var root)
                    ? new IndexRequest(root, verb == "index")
                    : Fail($"track-a {verb} needs --corpus with the repository's track-a folder.", out error);
        }
    }

    public static int Run(OsInfo os, Request request, AppInfo app, UserProfileRedactor profile) => request switch
    {
        CaptureRequestArgs capture => new CaptureFlow(os, app, profile, capture).Run(),
        VerifyRequest verify => CorpusFlows.Verify(verify, app, profile),
        PromoteRequest promote => CorpusFlows.Promote(promote, app, profile),
        IndexRequest index => CorpusFlows.Index(index, profile),
        _ => throw new InvalidOperationException("Unknown track-a request."),
    };

    private static Request? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }
}
