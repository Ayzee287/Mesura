using System.Security.Cryptography;
using System.Security.Principal;
using Mesura.Core.Support;
using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>
/// What cannot be shown with stand-ins: the real Windows access lists, the real unelevated token. They need an elevated process with a desktop shell, so on an ordinary developer session
/// they skip; they run in Windows Sandbox (tools\sandbox, scenario nvpi-elevated), whose account is an administrator. Each one creates only its own uniquely named folders and removes them.
/// </summary>
public sealed class NvidiaElevatedRealWindowsTests : IDisposable
{
    private const string NotElevated = "Needs an elevated process with a desktop shell (run in Windows Sandbox: tools\\sandbox, scenario nvpi-elevated).";
    private readonly string _purpose = "selftest" + Guid.NewGuid().ToString("N")[..6];
    private readonly string _probe = Directory.CreateTempSubdirectory("mesura-real-").FullName;
    private readonly List<string> _folders = [];

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(_probe, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Fresh()
    {
        var folder = ProtectedToolFolder.Fresh(_purpose);
        _folders.Add(folder);
        return folder;
    }

    /// <summary>Runs a batch file holding <paramref name="commands"/> with the signed-in user's own unelevated token; returns what <paramref name="output"/> says afterwards (or null when it wrote nothing).</summary>
    /// <summary>
    /// Runs <paramref name="commands"/> as a program that is NOT an administrator, which is what an attacker's code running as the user is: with the signed-in user's own unelevated token from the
    /// desktop shell, or, where the shell itself is elevated (Windows Sandbox: UAC off), as the standard local user the scenario made (MESURA_PROBE_USER / MESURA_PROBE_PASSWORD).
    /// It writes a control file in a folder every user can write to first, so "could not write there" cannot mean "the program never ran".
    /// </summary>
    private void AsStandardUser(string commands)
    {
        var control = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "mesura-control-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        var batchText = $"@echo off\r\necho ran> \"{control}\"\r\n{commands}\r\n";
        var batch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "mesura-run-" + Guid.NewGuid().ToString("N")[..6] + ".cmd");
        File.WriteAllText(batch, batchText);
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var viaShell = new ToolProcess().Run(cmd, ["/c", batch], Environment.SystemDirectory, asShellUser: true, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (!viaShell.Started)
            {
                var user = Environment.GetEnvironmentVariable("MESURA_PROBE_USER");
                var password = Environment.GetEnvironmentVariable("MESURA_PROBE_PASSWORD");
                Assert.SkipUnless(user is not null && password is not null, $"No unelevated token here ({viaShell.Error}) and no probe user: MESURA_PROBE_USER / MESURA_PROBE_PASSWORD.");
                var start = new System.Diagnostics.ProcessStartInfo(cmd) { UserName = user, Domain = Environment.MachineName, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory, CreateNoWindow = true, LoadUserProfile = false };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(batch);
                start.PasswordInClearText = password;
                using var process = System.Diagnostics.Process.Start(start)!;
                Assert.True(process.WaitForExit(30000), "the standard-user probe did not finish");
            }

            Assert.True(File.Exists(control), "the unprivileged program did not run at all (its control file is missing)");
            File.Delete(control);
        }
        finally
        {
            File.Delete(batch);
        }
    }

    private string? TryAsUser(string commands, string output, out ToolRun run)
    {
        var batch = Path.Combine(_probe, "run-" + Guid.NewGuid().ToString("N")[..6] + ".cmd");
        File.WriteAllText(batch, "@echo off\r\n" + commands + "\r\n");
        run = new ToolProcess().Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", batch], _probe, asShellUser: true, TimeSpan.FromSeconds(30), CancellationToken.None);
        return run.Started && File.Exists(output) ? File.ReadAllText(output).Trim() : null;
    }

    [Fact]
    public void The_token_taken_from_the_shell_is_unelevated_and_this_processs_own_is_not()
    {
        Assert.SkipUnless(Elevated(), NotElevated);
        // S-1-16-12288 is the High mandatory level: present in an elevated token, absent in the shell's.
        string Test(string file) => $"whoami /groups | find \"S-1-16-12288\" >nul\r\nif errorlevel 1 (echo unelevated> \"{file}\") else (echo elevated> \"{file}\")";
        var user = Path.Combine(_probe, "user.txt");
        var self = Path.Combine(_probe, "self.txt");
        var batch = Path.Combine(_probe, "self.cmd");
        File.WriteAllText(batch, "@echo off\r\n" + Test(self) + "\r\n");

        var asUser = TryAsUser(Test(user), user, out var run);
        var here = new ToolProcess().Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", batch], _probe, asShellUser: false, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(here.Started);
        Assert.Equal("elevated", File.ReadAllText(self).Trim());
        // Where the shell runs unelevated (UAC on) the program started with its token is unelevated. Where the shell is itself elevated (Windows Sandbox: UAC off) the token is refused and
        // nothing is started with it: it is never an elevated process presented as unelevated.
        Assert.NotEqual("elevated", asUser);
        Assert.True(asUser == "unelevated" || (!run.Started && run.Error!.Contains("itself running elevated", StringComparison.Ordinal)), $"started: {run.Started}, error: {run.Error}, wrote: {asUser}");
    }

    [Fact]
    public void A_folder_made_for_elevated_use_passes_the_trust_check_and_a_program_running_as_the_user_cannot_write_to_it()
    {
        Assert.SkipUnless(Elevated(), NotElevated);
        var folder = Fresh();

        Assert.Null(ProgramFolderTrust.Why(folder)); // the check Mesura applies before running anything from here elevated
        var wrote = Path.Combine(folder, "planted.exe");
        AsStandardUser($"echo planted> \"{wrote}\"");
        Assert.False(File.Exists(wrote)); // a program that is not an administrator cannot plant a file there (it did run: its control file was written)
    }

    [Fact]
    public void A_plain_folder_under_ProgramData_does_not_pass_which_is_why_the_driver_download_needed_a_protected_one()
    {
        Assert.SkipUnless(Elevated(), NotElevated);
        var plain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MesuraPlain" + _purpose);
        Directory.CreateDirectory(plain);
        _folders.Add(plain);

        Assert.NotNull(ProgramFolderTrust.Why(plain)); // Windows lets users create files in subfolders of ProgramData
    }

    [Fact]
    public void The_driver_folder_is_protected_and_stays_the_same_folder_between_runs()
    {
        Assert.SkipUnless(Elevated(), NotElevated);

        var first = ProtectedToolFolder.Persistent("drivers-" + _purpose);
        var second = ProtectedToolFolder.Persistent("drivers-" + _purpose);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        _folders.Add(first!);
        Assert.Null(ProgramFolderTrust.Why(first!));
    }

    [Fact]
    public void A_genuine_release_staged_from_a_user_folder_is_verified_and_cannot_be_altered_by_a_program_of_the_users()
    {
        Assert.SkipUnless(Elevated(), NotElevated);
        var sample = Environment.GetEnvironmentVariable("MESURA_NVPI_SAMPLE");
        Assert.SkipUnless(sample is not null && File.Exists(Path.Combine(sample, "nvidiaProfileInspector.exe")), "Set MESURA_NVPI_SAMPLE to a folder holding a genuine NVIDIA Profile Inspector release (the Sandbox scenario does).");
        var source = Path.Combine(_probe, "winget-user-scope");
        Directory.CreateDirectory(source);
        foreach (var file in Directory.GetFiles(sample!))
        {
            File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        }

        var stager = new ProfileInspectorStager(Fresh);
        var staged = stager.Stage(new ProfileInspector(Path.Combine(source, "nvidiaProfileInspector.exe"), new Version(3, 0, 2, 1)), TestContext.Current.CancellationToken);

        Assert.Null(staged.Refusal);
        var folder = Path.GetDirectoryName(staged.Inspector!.Path)!;
        Assert.Null(ProgramFolderTrust.Why(folder));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged.Inspector.Path))), ProfileInspectorStager.KnownReleases.Keys);

        // A program the user runs cannot append to the verified copy, or replace it.
        AsStandardUser($"echo tampered>> \"{staged.Inspector.Path}\"\r\ndel /f \"{staged.Inspector.Path}\"");
        Assert.True(File.Exists(staged.Inspector.Path));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged.Inspector.Path))), ProfileInspectorStager.KnownReleases.Keys);

        // And the copy of the user's own copy that was swapped for something else is refused on the real file system too.
        File.WriteAllText(Path.Combine(source, "nvidiaProfileInspector.exe"), "MZ not the release");
        Assert.NotNull(stager.Stage(new ProfileInspector(Path.Combine(source, "nvidiaProfileInspector.exe"), new Version(3, 0, 2, 1)), TestContext.Current.CancellationToken).Refusal);
    }
}
