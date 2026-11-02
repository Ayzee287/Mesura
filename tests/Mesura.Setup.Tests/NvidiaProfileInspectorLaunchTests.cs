using System.Security.Cryptography;
using System.Text;
using Mesura.Core.Nvidia;
using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>
/// How an elevated Mesura runs NVIDIA Profile Inspector, on the case that failed on the owner's PC (2026-11-02): the per-user winget package, where programs running as the user can replace
/// the executable. It must never be started with administrator rights as it is; an export (a read) is tried with the user's own token; a write goes through a verified copy in a folder only
/// administrators can write to. A stand-in process records every start, so each test shows what was started, with which rights, and from where.
/// </summary>
public sealed class NvidiaProfileInspectorLaunchTests : IDisposable
{
    private static readonly byte[] GenuineExe = Encoding.ASCII.GetBytes("MZ genuine NVIDIA Profile Inspector 3.0.2.1");
    private static readonly byte[] SwappedExe = Encoding.ASCII.GetBytes("MZ something else that was put there");
    private static readonly string GenuineHash = Convert.ToHexString(SHA256.HashData(GenuineExe));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mesura-nvpi-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _wingetUser;
    private readonly string _programFiles;
    private readonly List<string> _staged = [];

    public NvidiaProfileInspectorLaunchTests()
    {
        _wingetUser = Directory.CreateDirectory(Path.Combine(_root, "user", "WinGet", "Packages", "Orbmu2k.nvidiaProfileInspector_x")).FullName;
        _programFiles = Directory.CreateDirectory(Path.Combine(_root, "protected", "WinGet", "Packages", "Orbmu2k.nvidiaProfileInspector_x")).FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ProfileInspector Place(string folder, byte[] exe, Version? version = null)
    {
        var path = Path.Combine(folder, "nvidiaProfileInspector.exe");
        File.WriteAllBytes(path, exe);
        return new ProfileInspector(path, version ?? new Version(3, 0, 2, 1));
    }

    private string? Trust(string folder) => folder.StartsWith(Path.Combine(_root, "protected"), StringComparison.OrdinalIgnoreCase) || _staged.Any(s => folder.StartsWith(s, StringComparison.OrdinalIgnoreCase))
        ? null
        : "any program you run can change it";

    private sealed class FakeVerifier(bool official = false, string reason = "it is not the file published in release 3.0.2.1") : IReleaseVerifier
    {
        public List<string> Asked { get; } = [];

        public bool Verify(Version version, string executableSha256, CancellationToken cancellationToken, out string why)
        {
            Asked.Add(executableSha256);
            why = official ? "" : reason;
            return official;
        }
    }

    private ProfileInspectorStager Stager(IReadOnlyDictionary<string, string>? known = null, IReleaseVerifier? verifier = null) => new(() =>
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "staged-" + _staged.Count)).FullName;
        _staged.Add(folder);
        return folder;
    }, known ?? new Dictionary<string, string> { [GenuineHash] = "3.0.2.1" }, verifier: verifier ?? new FakeVerifier());

    private NvidiaTools Tools(FakeToolProcess process, bool elevated = true, ProfileInspectorStager? stager = null) => new(() => elevated, process, Trust, stager ?? Stager());

    private static byte[] Nip(string value) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes($"""
        <?xml version="1.0" encoding="utf-16"?><ArrayOfProfile><Profile><ProfileName>Some Game</ProfileName><Executeables><string>game.exe</string></Executeables>
        <Settings><ProfileSetting><SettingNameInfo /><SettingID>11306135</SettingID><SettingValue>{value}</SettingValue><ValueType>Dword</ValueType></ProfileSetting></Settings></Profile></ArrayOfProfile>
        """)];

    private static byte[] EmptyNip() => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("""<?xml version="1.0" encoding="utf-16"?><ArrayOfProfile />""")];

    private sealed record Call(string Executable, string[] Arguments, string Directory, bool AsShellUser, byte[]? ImportedBytes, string? ImportedFrom);

    private sealed class FakeToolProcess : IToolProcess
    {
        public List<Call> Calls { get; } = [];

        /// <summary>What an export writes beside the tool, by who runs it: null means it writes nothing.</summary>
        public Func<bool, byte[]?> Exported { get; set; } = _ => Nip("10240");

        /// <summary>The result of starting it, by who runs it (the user's token, or this process's).</summary>
        public Func<bool, ToolRun> Outcome { get; set; } = _ => new ToolRun(true, false, 0, null);

        public ToolRun Run(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool asShellUser, TimeSpan timeout, CancellationToken cancellationToken)
        {
            byte[]? imported = null;
            string? importedFrom = null;
            if (arguments.Count == 3 && arguments[1] == "-mergeImport")
            {
                importedFrom = arguments[2];
                imported = File.ReadAllBytes(arguments[2]);
            }

            Calls.Add(new Call(executable, [.. arguments], workingDirectory, asShellUser, imported, importedFrom));
            var outcome = Outcome(asShellUser);
            if (outcome.Started && arguments.Count == 1 && arguments[0] == "-exportCustomized" && Exported(asShellUser) is { } export)
            {
                File.WriteAllBytes(Path.Combine(workingDirectory, $"CustomProfiles_{Calls.Count}.nip"), export);
            }

            return outcome;
        }
    }

    [Fact]
    public void A_winget_user_scope_copy_is_exported_with_the_users_own_token_and_never_started_elevated_as_it_is()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var process = new FakeToolProcess();

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.NotNull(export.Content);
        var call = Assert.Single(process.Calls);
        Assert.True(call.AsShellUser); // the unelevated user token, not the elevated one
        Assert.Equal(inspector.Path, call.Executable);
        Assert.Empty(_staged); // nothing was copied: an export only reads
        Assert.Empty(Directory.GetFiles(_wingetUser, "CustomProfiles_*")); // the export file this run caused is removed once kept
    }

    [Fact]
    public void When_the_unelevated_export_gives_nothing_a_verified_copy_in_a_protected_folder_is_run_instead()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var process = new FakeToolProcess { Exported = asUser => asUser ? null : Nip("10240") };

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.NotNull(export.Content);
        Assert.Equal(2, process.Calls.Count);
        Assert.True(process.Calls[0].AsShellUser);
        Assert.False(process.Calls[1].AsShellUser);
        Assert.StartsWith(_staged.Single(), process.Calls[1].Executable, StringComparison.OrdinalIgnoreCase); // the copy, not the user's file
        Assert.NotEqual(inspector.Path, process.Calls[1].Executable);
        Assert.False(Directory.Exists(_staged.Single())); // removed afterwards
    }

    [Fact]
    public void An_empty_list_read_with_the_users_rights_is_confirmed_by_the_verified_copy_before_it_counts_as_nothing_to_back_up()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var process = new FakeToolProcess { Exported = asUser => asUser ? EmptyNip() : Nip("10240") }; // the unelevated read saw nothing; the elevated one sees a setting

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.Equal(1, NipFile.SettingCount(NipFile.Parse(NipFile.Decode(export.Content!))));
        Assert.False(process.Calls[^1].AsShellUser);
    }

    [Fact]
    public void An_empty_list_stands_when_no_verified_copy_can_be_made_because_nothing_else_was_read()
    {
        var inspector = Place(_wingetUser, SwappedExe); // not a build Mesura knows: no copy to run
        var process = new FakeToolProcess { Exported = _ => EmptyNip() };

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.NotNull(export.Content); // read with the user's own rights, empty, said as read
        Assert.All(process.Calls, c => Assert.True(c.AsShellUser));
    }

    [Fact]
    public void An_unknown_build_in_a_user_folder_is_not_exported_at_all_when_the_user_token_cant_read_it_and_the_message_says_why()
    {
        var inspector = Place(_wingetUser, SwappedExe);
        var process = new FakeToolProcess { Outcome = asUser => asUser ? new ToolRun(false, false, 0, "the desktop shell isn't running") : new ToolRun(true, false, 0, null) };

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.Null(export.Content);
        Assert.Contains("can't be confirmed as the official release", export.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Calls, c => !c.AsShellUser); // never started with administrator rights
    }

    [Fact]
    public void A_copy_in_a_folder_only_administrators_can_change_runs_as_it_is_even_elevated()
    {
        var inspector = Place(_programFiles, GenuineExe);
        var process = new FakeToolProcess();

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.NotNull(export.Content);
        var call = Assert.Single(process.Calls);
        Assert.False(call.AsShellUser);
        Assert.Equal(inspector.Path, call.Executable);
        Assert.Empty(_staged);
    }

    [Fact]
    public void Without_elevation_the_tool_runs_as_it_is_with_the_users_own_rights()
    {
        var inspector = Place(_wingetUser, SwappedExe); // trust is not an issue: the tool has only the user's rights
        var process = new FakeToolProcess();

        var export = Tools(process, elevated: false).Export(inspector, CancellationToken.None);

        Assert.NotNull(export.Content);
        Assert.Equal(inspector.Path, Assert.Single(process.Calls).Executable);
        Assert.Empty(_staged);
    }

    [Fact]
    public void An_import_from_a_winget_user_copy_runs_a_verified_copy_on_a_protected_copy_of_the_file()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("20480"));
        var process = new FakeToolProcess();

        var error = Tools(process).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Null(error);
        var call = Assert.Single(process.Calls);
        Assert.False(call.AsShellUser);
        Assert.StartsWith(_staged.Single(), call.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_staged.Single(), call.ImportedFrom!, StringComparison.OrdinalIgnoreCase); // the elevated tool reads the protected copy, not the user's file
        Assert.Equal(File.ReadAllBytes(nip), call.ImportedBytes);
        Assert.Equal(["-silent", "-mergeImport", call.ImportedFrom!], call.Arguments);
        Assert.False(Directory.Exists(_staged.Single()));
    }

    [Fact]
    public void An_import_never_runs_an_executable_that_was_replaced_and_names_what_it_found()
    {
        var inspector = Place(_wingetUser, SwappedExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("20480"));
        var process = new FakeToolProcess();

        var error = Tools(process).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Empty(process.Calls); // nothing started: whoever replaced the file would have got administrator rights
        Assert.Contains("can't be confirmed as the official release", error, StringComparison.Ordinal);
        Assert.Contains("doesn't run it with administrator rights", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(SwappedExe)), error, StringComparison.Ordinal); // no hash for the user to read
        Assert.False(Directory.Exists(_staged.Single())); // the unverified copy is removed too
    }

    [Fact]
    public void A_file_replaced_after_the_tool_was_found_is_caught_because_the_copy_is_hashed_when_it_is_made()
    {
        var inspector = Place(_wingetUser, GenuineExe); // found as the genuine build ...
        File.WriteAllBytes(inspector.Path, SwappedExe); // ... swapped before the elevated start
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess();

        var error = Tools(process).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Empty(process.Calls);
        Assert.Contains("can't be confirmed as the official release", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_build_nobody_listed_is_trusted_when_it_is_exactly_what_the_project_published_for_its_version()
    {
        var newer = Encoding.ASCII.GetBytes("MZ NVIDIA Profile Inspector 3.0.2.9, a release Mesura has no hash for");
        var inspector = Place(_wingetUser, newer, new Version(3, 0, 2, 9));
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("20480"));
        var verifier = new FakeVerifier(official: true);
        var process = new FakeToolProcess();

        var error = Tools(process, stager: Stager(verifier: verifier)).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Null(error); // nobody had to add a hash
        Assert.Equal([Convert.ToHexString(SHA256.HashData(newer))], verifier.Asked);
        Assert.StartsWith(_staged.Single(), Assert.Single(process.Calls).Executable, StringComparison.OrdinalIgnoreCase); // still the protected copy that runs
    }

    [Fact]
    public void A_listed_build_needs_no_network_at_all()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var verifier = new FakeVerifier();

        Assert.Null(Tools(new FakeToolProcess(), stager: Stager(verifier: verifier)).MergeImport(inspector, nip, CancellationToken.None));

        Assert.Empty(verifier.Asked);
    }

    [Fact]
    public void Without_a_connection_an_unlisted_build_is_not_run_as_administrator_and_the_message_does_not_blame_the_user()
    {
        var inspector = Place(_wingetUser, SwappedExe, new Version(3, 0, 2, 9));
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess();

        var error = Tools(process, stager: Stager(verifier: new FakeVerifier(reason: "GitHub could not be reached to check it"))).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Empty(process.Calls);
        Assert.Contains("GitHub could not be reached", error, StringComparison.Ordinal);
        Assert.Contains("connect to the internet", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_unreadable_executable_is_refused_before_anything_is_started()
    {
        var gone = new ProfileInspector(Path.Combine(_wingetUser, "nvidiaProfileInspector.exe"), new Version(3, 0, 2, 1));
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess();

        Assert.NotNull(Tools(process).MergeImport(gone, nip, CancellationToken.None));

        var locked = Place(_wingetUser, GenuineExe);
        using (new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // another program holds it exclusively
        {
            var error = Tools(process).MergeImport(locked, nip, CancellationToken.None);
            Assert.Contains("couldn't be read", error, StringComparison.Ordinal);
        }

        Assert.Empty(process.Calls);
    }

    [Fact]
    public void Files_beside_the_executable_that_are_not_the_releases_own_are_left_out_of_the_copy()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        File.WriteAllText(Path.Combine(_wingetUser, "nvidiaProfileInspector.exe.config"), "<configuration><runtime>planted</runtime></configuration>");
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess();
        string[] seen = [];
        process.Outcome = _ =>
        {
            seen = [.. Directory.GetFiles(Path.GetDirectoryName(process.Calls[^1].Executable)!).Select(Path.GetFileName)!];
            return new ToolRun(true, false, 0, null);
        };

        Assert.Null(Tools(process).MergeImport(inspector, nip, CancellationToken.None));

        Assert.DoesNotContain("nvidiaProfileInspector.exe.config", seen);
        Assert.Contains("nvidiaProfileInspector.exe", seen);
    }

    [Fact]
    public void A_refusal_to_elevate_or_a_tool_that_does_not_start_is_an_error_not_a_success()
    {
        var inspector = Place(_wingetUser, GenuineExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess { Outcome = _ => new ToolRun(false, false, 0, "The operation was canceled by the user") };

        var error = Tools(process).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Contains("didn't start", error, StringComparison.Ordinal);
        Assert.Contains("canceled by the user", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_that_shows_a_message_and_never_finishes_is_reported_and_left_for_the_user()
    {
        var inspector = Place(_programFiles, GenuineExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess { Outcome = _ => new ToolRun(true, true, 0, null) };

        var error = Tools(process).MergeImport(inspector, nip, CancellationToken.None);

        Assert.Contains("may be showing a message", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_that_wrote_no_file_is_an_error_and_never_empty_content()
    {
        var inspector = Place(_programFiles, GenuineExe);
        var process = new FakeToolProcess { Exported = _ => null };

        var export = Tools(process).Export(inspector, CancellationToken.None);

        Assert.Null(export.Content);
        Assert.Contains("without writing an export", export.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void After_the_cause_is_fixed_the_same_tool_works_on_the_next_try()
    {
        var inspector = Place(_wingetUser, SwappedExe);
        var nip = Path.Combine(_root, "backup.nip");
        File.WriteAllBytes(nip, Nip("1"));
        var process = new FakeToolProcess();
        var tools = Tools(process);

        Assert.NotNull(tools.MergeImport(inspector, nip, CancellationToken.None)); // refused
        Assert.Empty(process.Calls);

        File.WriteAllBytes(inspector.Path, GenuineExe); // the genuine release is put back
        Assert.Null(tools.MergeImport(inspector, nip, CancellationToken.None));
        Assert.Single(process.Calls);
    }

    [Fact]
    public void The_hashes_Mesura_trusts_are_the_published_releases_and_nothing_else()
    {
        Assert.Equal(["3.0.2.1", "3.0.2.2", "3.0.2.3"], ProfileInspectorStager.KnownReleases.Values.Order());
        Assert.All(ProfileInspectorStager.KnownReleases.Keys, h => Assert.Equal(64, h.Length));
    }

    [Fact]
    public void A_folder_made_for_elevated_use_grants_users_nothing_but_reading()
    {
        var grants = ProtectedToolFolder.Grants();
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);

        Assert.Equal(System.Security.AccessControl.FileSystemRights.ReadAndExecute, grants.Single(g => g.Who.Equals(users)).Rights);
        Assert.All(grants.Where(g => !g.Who.Equals(users)), g => Assert.Equal(System.Security.AccessControl.FileSystemRights.FullControl, g.Rights));
    }
}
