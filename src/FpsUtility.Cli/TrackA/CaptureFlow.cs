using System.Diagnostics;
using System.Globalization;
using FpsUtility.Core.Privacy;
using FpsUtility.Core.Raw;
using FpsUtility.Core.Raw.Serialization;
using FpsUtility.Core.TrackA;
using FpsUtility.Platform.Windows;

namespace FpsUtility.Cli.TrackA;

/// <summary>
/// <c>track-a capture</c>: one scenario on this PC, as one session (two for the PCIe spike). The order is fixed:
/// setup and the operator's disposition, the scan, the pure pipeline, the machine summary, the evidence, the questions,
/// then the privacy verification of the whole package. Only a verified package is written, manifest last.
/// </summary>
internal sealed partial class CaptureFlow(OsInfo os, AppInfo app, UserProfileRedactor profile, TrackACli.CaptureRequestArgs args)
{
    private const int DefaultDelaySeconds = 20;

    private readonly Operator _operator = new(profile);

    public int Run()
    {
        var say = _operator.Say;
        var output = Path.GetFullPath(args.Out);

        if (Program.InsideGitRepository(output))
        {
            say($"The output folder {output} is inside a git repository. A capture can hold unscrubbed data before it is verified, so it stays outside git: copy the kit to a USB stick or to ..\\research\\track-a\\kit and run it there.");
            return 2;
        }

        if (!Directory.Exists(output))
        {
            var parent = Path.GetDirectoryName(output);
            var name = Path.GetFileName(output);
            if (parent is null || !Directory.Exists(parent))
            {
                say($"Neither the output folder {output} nor its parent exists.");
                return 2;
            }

            if (!PlainFolderName().IsMatch(name))
            {
                say($"The kit creates an output folder only with a plain name (letters, digits, '.', '_', '-'); '{name}' isn't one. Create the folder yourself, or choose another.");
                return 2;
            }

            LocalStore.CreateExportFolder(parent, name);
        }

        Header(output);
        var identifiers = LocalIdentifiers.Read();

        try
        {
            var cohortId = args.Cohort ?? AskCohort(output);
            var scenario = args.Scenario is { } id ? ScenarioCatalog.Require(id) : AskScenario();
            say();
            say($"{scenario.Id}: {scenario.Summary}");

            var exit = 0;
            ManifestSeries? series = null;
            CapturePreparation? idle = null;
            DateTimeOffset? idleAt = null;

            foreach (var step in scenario.Steps)
            {
                var (code, disposition, prepared, sessionId) = Step(output, cohortId, scenario, step, series, idle, idleAt, identifiers);
                exit = Worse(exit, code);
                if (code == 4 || disposition != ScenarioDisposition.Performed || prepared is null)
                {
                    break; // a privacy failure, or a first step declared not applicable or skipped, ends the scenario
                }

                if (step.Name == ScenarioCatalog.IdleStep)
                {
                    series = new ManifestSeries(sessionId, null, null, null);
                    idle = prepared;
                    idleAt = prepared.Scrubbed.CapturedAtUtc;
                }
            }

            say();
            say("Nothing was sent anywhere. The kit never uploads.");
            return exit;
        }
        catch (OperatorInputEndedException ex)
        {
            say(ex.Message + " The current session wasn't packaged.");
            return 1;
        }
    }

    /// <summary>The run's exit code is its worst step's: a privacy failure (4), then a stop (1), then not acceptable (6), then ready (0).</summary>
    private static int Worse(int a, int b)
    {
        static int Rank(int code) => code switch { 4 => 3, 1 => 2, TrackACli.NotReady => 1, _ => 0 };
        return Rank(b) > Rank(a) ? b : a;
    }

    private void Header(string output)
    {
        var say = _operator.Say;
        say("FPS Utility - Track A capture kit");
        say("Internal engineering validation of a known PC: it reads this PC, changes nothing and sends nothing.");
        say(app.Commit.IsKnown
            ? $"Build {app.Version}, commit {app.Commit.Value}."
            : $"Build {app.Version}, no single commit ({app.Commit.Detail}). Captures from this build can't be accepted into the corpus.");
        var family = ManifestPlatform.FamilyOf(os.Build) == WindowsFamily.Windows11 ? "Windows 11" : "Windows 10 (22H2 or later)";
        say(string.Create(CultureInfo.InvariantCulture, $"{family}, build {os.Build}.{(os.UpdateRevision.IsKnown ? os.UpdateRevision.Value.ToString(CultureInfo.InvariantCulture) : "?")}: a supported platform (D008)."));
        if (ManifestPlatform.FamilyOf(os.Build) == WindowsFamily.Windows10)
        {
            say("  On Windows 10 22H2 the .NET 10 runtime runs outside Microsoft's support; this cohort is where FPS Utility is proven on it.");
        }

        say($"Output: {output}");
        if (UnderOneDrive(output))
        {
            say("  Warning: this folder is synced by OneDrive, so what is written here is uploaded by OneDrive (not by the kit). Prefer a USB stick or a local folder.");
        }
    }

    private string AskCohort(string output)
    {
        var existing = Directory.Exists(output)
            ? Directory.EnumerateDirectories(output).Select(Path.GetFileName).OfType<string>().Where(CohortId.IsValid).Order(StringComparer.Ordinal).ToList()
            : [];
        if (existing.Count > 0)
        {
            _operator.Say();
            _operator.Say("Cohort ids already on this kit: " + string.Join(", ", existing.Select(id =>
                string.Create(CultureInfo.InvariantCulture, $"{id} ({Directory.EnumerateDirectories(Path.Combine(output, id)).Count()} sessions)"))));
        }

        while (true)
        {
            var answer = _operator.Ask("Cohort id of this PC, from the owner's enrolment list (PC-NNN): ").Trim().ToUpperInvariant();
            if (CohortId.IsValid(answer))
            {
                return answer;
            }

            _operator.Say("  A Track A cohort id is PC and three digits, for example PC-001. It is never derived from the machine.");
        }
    }

    private Scenario AskScenario()
    {
        _operator.Say();
        _operator.Say("Scenario:");
        for (var i = 0; i < ScenarioCatalog.All.Count; i++)
        {
            _operator.Say(string.Create(CultureInfo.InvariantCulture, $"  {i + 1} {ScenarioCatalog.All[i].Id,-21} {ScenarioCatalog.All[i].Summary}"));
        }

        while (true)
        {
            var answer = _operator.Ask("Choose [Enter = 1 baseline]: ").Trim();
            if (answer.Length == 0)
            {
                return ScenarioCatalog.All[0];
            }

            if (int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1 && index <= ScenarioCatalog.All.Count)
            {
                return ScenarioCatalog.All[index - 1];
            }

            if (ScenarioCatalog.Find(answer) is { } scenario)
            {
                return scenario;
            }
        }
    }

    private (int Exit, ScenarioDisposition Disposition, CapturePreparation? Prepared, string SessionId) Step(
        string output,
        string cohortId,
        Scenario scenario,
        ScenarioStep step,
        ManifestSeries? idleSeries,
        CapturePreparation? idle,
        DateTimeOffset? idleAt,
        (IReadOnlyList<LocalIdentifier> Identifiers, IReadOnlyList<string> Unread) identifiers)
    {
        var say = _operator.Say;
        var sessionId = SessionId.At(Now());
        var cohortFolder = LocalStore.CreateExportFolder(output, cohortId);
        if (Directory.Exists(Path.Combine(cohortFolder, sessionId)))
        {
            // Two sessions can't share a second; wait for the next one rather than touch an existing session.
            Thread.Sleep(1100);
            sessionId = SessionId.At(Now());
        }

        var session = LocalStore.CreateExportFolder(cohortFolder, sessionId);
        var evidence = LocalStore.CreateExportFolder(session, "evidence");

        say();
        say($"-- {cohortId} - {sessionId}{(step.Name is { } name ? " - step " + name : "")} --");
        Operator.SayExact($"Evidence folder: {evidence}");
        foreach (var line in step.Setup)
        {
            Operator.SayExact("  " + line.Replace("{evidence}", evidence, StringComparison.Ordinal));
        }

        var disposition = AskDisposition(step);
        if (disposition != ScenarioDisposition.Performed && step.InGame)
        {
            say("  Recorded as declared; the scan still runs so the capture shows the PC as it is.");
        }

        var started = Stopwatch.StartNew();
        RawSnapshot raw;
        RawSnapshot? repeat = null;
        if (step.InGame && disposition == ScenarioDisposition.Performed)
        {
            var delay = args.DelaySeconds ?? DefaultDelaySeconds;
            say(string.Create(CultureInfo.InvariantCulture, $"  Switch to the game now. The scan starts in {delay} s; a beep marks its start and another its end."));
            for (var left = delay; left > 0; left -= 5)
            {
                say(string.Create(CultureInfo.InvariantCulture, $"    {left} s"));
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(5, left)));
            }

            Beep();
            started.Restart();
            raw = SnapshotCollector.Collect(os, app);
            Beep();
            say(string.Create(CultureInfo.InvariantCulture, $"  Scan done: the load window lasted {started.Elapsed.TotalSeconds:0.0} s. You can come back to this window."));
        }
        else
        {
            say("  Scanning...");
            raw = SnapshotCollector.Collect(os, app);
            if (args.RepeatScan)
            {
                repeat = SnapshotCollector.Collect(os, app);
            }

            say(string.Create(CultureInfo.InvariantCulture, $"  Scan done in {started.Elapsed.TotalSeconds:0.0} s{(repeat is null ? "" : ", with a repeat scan for the repeatability record")}."));
        }

        if (args.KeepPrivate)
        {
            var privateFolder = LocalStore.CreateExportFolder(session, "private");
            LocalStore.WriteUserExport(Path.Combine(privateFolder, "raw.json"), SnapshotJson.Serialize(raw));
            say($"  Kept the unscrubbed scan in {privateFolder} (--keep-private). It never leaves this kit through it: delete it before the kit visits another PC.");
        }

        CapturePreparation prepared;
        try
        {
            prepared = CapturePipeline.Prepare(raw, repeat, profile.Redact);
        }
#pragma warning disable CA1031 // Without the scrub nothing is shareable; the session ends with no package.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            say($"  The scrub failed ({ex.GetType().Name}), so nothing shareable can be made from this scan. Nothing was written to shareable.");
            return (1, disposition, null, sessionId);
        }

        say();
        if (prepared.Facts is { } facts)
        {
            foreach (var line in MachineSummary.Lines(facts, prepared.Scrubbed))
            {
                say(line);
            }
        }

        foreach (var stage in prepared.Stages.Where(stage => stage.Status == StageStatus.Failed))
        {
            say($"  Stage {stage.Stage} failed: {stage.Detail}. The package will carry the scrubbed scan so the defect can be reproduced; it can't be accepted.");
        }

        var unavailable = scenario.RequiredSources.Where(source => MachineSummary.Unavailable(prepared.Scrubbed).Any(entry => entry.StartsWith(source + " ", StringComparison.Ordinal))).ToList();
        if (disposition == ScenarioDisposition.Performed && unavailable.Count > 0)
        {
            say($"  This scenario needs {string.Join(", ", unavailable)}, which couldn't be read here: it is recorded as unavailable (machine-observed).");
        }

        // External evidence, collected by the operator while the state lasts.
        if (disposition == ScenarioDisposition.Performed && step.AfterScan.Count > 0)
        {
            say();
            say("Now, before the questions:");
            foreach (var line in step.AfterScan)
            {
                Operator.SayExact("  " + line.Replace("{evidence}", evidence, StringComparison.Ordinal));
            }

            EvidenceLoop(evidence, step);
        }

        // The questions.
        var screenWith = identifiers.Identifiers.Concat(prepared.OpaqueIdentifiers).ToList();
        var answers = new List<Observation>();
        var gameNamesPossible = prepared.HoldsSteamGames || (disposition == ScenarioDisposition.Performed && step.Fields.Contains(ObservationCatalog.GameName));
        say();
        say("Questions: only what the software can't read. Enter means unknown; nobody has to guess.");
        if (gameNamesPossible && _operator.AskField(ObservationCatalog.GameNamesConsent, screenWith) is { } consent)
        {
            answers.Add(consent);
        }

        if (disposition == ScenarioDisposition.Performed)
        {
            foreach (var field in step.Fields)
            {
                if (_operator.AskField(field, screenWith) is { } answer)
                {
                    answers.Add(answer);
                }
            }
        }

        if (_operator.AskField(ObservationCatalog.OperatorNote, screenWith) is { } note)
        {
            answers.Add(note);
        }

        var observations = new OperatorObservations(OperatorObservations.CurrentSchemaVersion, cohortId, sessionId, scenario.Id, answers, step.Name);

        ManifestSeries? series = null;
        if (step.Name == ScenarioCatalog.IdleStep)
        {
            series = new ManifestSeries(sessionId, null, null, null);
        }
        else if (step.Name == ScenarioCatalog.LoadStep && idleSeries is not null)
        {
            bool? sameModels = idle?.Facts is { } before && prepared.Facts is { } after
                ? SessionTraits.From(before).ModelSignature == SessionTraits.From(after).ModelSignature
                : null;
            series = new ManifestSeries(idleSeries.Id, idleSeries.Id, idleAt is { } at ? (long)(prepared.Scrubbed.CapturedAtUtc - at).TotalSeconds : null, sameModels);
            if (sameModels is false)
            {
                say("  The load scan shows different graphics or machine models from the idle scan. It is recorded; check nothing was swapped.");
            }
        }

        var outcome = CapturePipeline.Package(new CaptureRequest(
            cohortId, sessionId, scenario.Id, step.Name, series, disposition, prepared, observations,
            ReadEvidence(evidence), identifiers.Identifiers, identifiers.Unread, args.KeepPrivate));

        return outcome switch
        {
            CapturedPackage package => (Write(session, package), disposition, prepared, sessionId),
            PrivacyFailure failure => (Refuse(session, failure), disposition, prepared, sessionId),
            _ => throw new InvalidOperationException("Unknown capture outcome."),
        };
    }

    private ScenarioDisposition AskDisposition(ScenarioStep step)
    {
        while (true)
        {
            var answer = _operator.Ask(step.InGame
                ? "When you are in stable gameplay, come back and press Enter - n = not applicable to this PC - s = skip: "
                : "Press Enter when the PC is set up as described - n = not applicable to this PC - s = skip: ").Trim();
            switch (answer.ToUpperInvariant())
            {
                case "":
                    return ScenarioDisposition.Performed;
                case "N" or "N/A" or "NA":
                    return ScenarioDisposition.NotApplicable;
                case "S":
                    return ScenarioDisposition.Skipped;
            }
        }
    }

    private void EvidenceLoop(string evidence, ScenarioStep step)
    {
        while (true)
        {
            var names = Directory.EnumerateFiles(evidence).Select(Path.GetFileName).OfType<string>().ToList();
            var named = names.Select(file => (File: file, Name: EvidenceConvention.Read(file))).Where(pair => pair.Name is not null).ToList();
            var ignored = names.Count - named.Count;

            _operator.Say();
            _operator.Say($"Evidence found: {(named.Count == 0 ? "none" : string.Join(", ", named.Select(pair => pair.File)))}{(ignored > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {ignored} other file(s) don't follow the naming convention and won't be recorded") : "")}.");
            var missing = step.Evidence.Where(expected => !named.Any(pair => pair.Name!.Tool == expected.Tool && EvidenceConvention.BelongsTo(pair.Name.State, step.Name))).ToList();
            if (missing.Count == 0)
            {
                _operator.Say("  Every expected kind is there.");
            }
            else
            {
                _operator.Say($"  Still expected: {string.Join(", ", missing.Select(expected => expected.Tool))}.");
            }

            var answer = _operator.Ask("  [Enter] look again - d = done (missing evidence is recorded as missing): ").Trim();
            if (answer.Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static List<EvidenceFile> ReadEvidence(string evidence) =>
        [.. Directory.EnumerateFiles(evidence).Order(StringComparer.OrdinalIgnoreCase).Select(path => new EvidenceFile(Path.GetFileName(path), File.ReadAllBytes(path)))];

    private int Write(string session, CapturedPackage package)
    {
        var say = _operator.Say;
        var shareable = LocalStore.CreateExportFolder(session, "shareable");
        foreach (var (name, bytes) in package.Files.Where(pair => pair.Key != PackageFiles.Manifest).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            LocalStore.WriteUserExport(Path.Combine(shareable, name), bytes);
        }

        LocalStore.WriteUserExport(Path.Combine(shareable, PackageFiles.Manifest), package.Files[PackageFiles.Manifest]);

        var manifest = package.Manifest;
        say();
        say($"SHAREABLE package written: {shareable}");
        say($"  Privacy: scrubbed ({manifest.Privacy.IdsReplaced} opaque ids replaced); none of this PC's {manifest.Privacy.IdentifierKindsChecked} identifier kinds and none of the scan's {manifest.Privacy.OpaqueIdsChecked} opaque ids found in any file; the audit found nothing.");
        if (manifest.Privacy.UncheckedIdentifierKinds.Count > 0)
        {
            say($"  Couldn't be read on this PC, so not checked: {string.Join(", ", manifest.Privacy.UncheckedIdentifierKinds)}. The owner acknowledges them at acceptance.");
        }

        say($"  Checks run: {manifest.Checks.Ran.Count} of {FpsUtility.Core.Checks.CheckIds.All.Count} ({(manifest.Checks.Ran.Count == 0 ? "none" : string.Join(", ", manifest.Checks.Ran))}). {manifest.Checks.NotImplemented.Count} aren't implemented: this is not a pass of all checks.");
        say($"  Scenario status: {System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(manifest.Scenario.Status.ToString())}.");
        say(manifest.RepeatScan.Performed
            ? string.Create(CultureInfo.InvariantCulture, $"  Repeat scan: {manifest.RepeatScan.ExpectedDifferences} run-dependent difference(s), {manifest.RepeatScan.OtherDifferenceCount} other(s); check results {(manifest.RepeatScan.Results == Comparison.Identical ? "identical" : "differ")}.")
            : "  Repeat scan: not taken.");
        if (manifest.Evidence.Ignored > 0)
        {
            say($"  {manifest.Evidence.Ignored} file(s) in evidence\\ don't follow the naming convention: they aren't recorded or reviewed. Delete or rename them before handing evidence\\ over.");
        }

        var flagged = manifest.Evidence.Files.Where(file => file.TextScan == EvidenceTextScan.IdentifierFound).Select(file => file.File).ToList();
        if (flagged.Count > 0)
        {
            say($"  Evidence holding this PC's identifiers or device ids: {string.Join(", ", flagged)} (the probe's output always holds device ids). Evidence is research material: it goes only to the owner's research folder, never into git. Leave it behind if that isn't wanted.");
        }

        if (manifest.Package.Status == PackageStatus.Ready)
        {
            say("  Ready for acceptance. Hand over shareable\\ (verified) and evidence\\ (unverified, reviewed by hand at acceptance), never private\\; then delete this session from the kit before it visits another PC.");
            return 0;
        }

        say($"  NOT READY for acceptance: {string.Join(", ", manifest.Package.Reasons)}. It is privacy-verified, so the owner may still read it, but it can't join the corpus as it is.");
        return TrackACli.NotReady;
    }

    private int Refuse(string session, PrivacyFailure failure)
    {
        var say = _operator.Say;
        LocalStore.WriteUserExport(Path.Combine(session, "privacy-failure.json"), failure.Json);
        say();
        say("NO shareable package: the privacy verification found data that must not leave this PC.");
        foreach (var finding in failure.Report.Findings)
        {
            say($"  {finding.File} at {(finding.Pointer.Length == 0 ? "(the whole file)" : finding.Pointer)}: {finding.Category}. {finding.Remediation}");
        }

        say($"  The report (no values in it) is {Path.Combine(session, "privacy-failure.json")}.");
        return 4;
    }

    private static bool UnderOneDrive(string path) =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Any(root => root.Length > 3 && path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlainFolderName();

    private static void Beep()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.Beep(880, 300);
        }
    }
}
