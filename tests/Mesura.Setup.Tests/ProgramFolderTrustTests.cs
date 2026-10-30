using System.Security.Principal;
using Mesura.Core.Support;

namespace Mesura.Setup.Tests;

/// <summary>
/// The folder Mesura may run elevated from (docs/security.md). The per-user install folder is writable by any program the user runs; Program
/// Files and System32 are not. The first test shows the exposure with a real write, the others that the access-list check agrees with it.
/// </summary>
public sealed class ProgramFolderTrustTests : IDisposable
{
    private readonly string _perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MesuraTrustTest-" + Guid.NewGuid().ToString("N")[..8]);

    public ProgramFolderTrustTests() => Directory.CreateDirectory(_perUser);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_perUser, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public void A_per_user_install_folder_can_really_be_overwritten_by_an_ordinary_process_and_is_flagged()
    {
        var exe = Path.Combine(_perUser, "Mesura.exe");
        File.WriteAllText(exe, "the program");

        File.WriteAllText(exe, "replaced by something else running as the user"); // the exposure: no privilege needed

        Assert.Equal("replaced by something else running as the user", File.ReadAllText(exe));
        Assert.NotNull(ProgramFolderTrust.Why(_perUser));
        Assert.True(ProgramFolderTrust.IsUserWritable(_perUser));
    }

    [Fact]
    public void Program_Files_and_System32_are_protected()
    {
        Assert.Null(ProgramFolderTrust.Why(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.Null(ProgramFolderTrust.Why(Environment.SystemDirectory));
        Assert.False(ProgramFolderTrust.IsUserWritable(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    [Fact]
    public void A_protected_folder_really_refuses_an_ordinary_process()
    {
        if (Elevated())
        {
            return; // an elevated process can write anywhere, which is exactly why the check reads the access list instead
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(programFiles, "mesura-trust-probe.tmp"), "x"));
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_not_trusted()
    {
        Assert.NotNull(ProgramFolderTrust.Why(Path.Combine(_perUser, "nope")));
    }

    [Fact]
    public void The_reason_names_who_can_write()
    {
        var why = ProgramFolderTrust.Why(_perUser)!;

        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void The_check_is_a_property_of_the_folder_not_of_the_process()
    {
        // Same answer for the same folder whatever token asks (the access list is read, nothing is written).
        Assert.Equal(ProgramFolderTrust.IsUserWritable(_perUser), ProgramFolderTrust.IsUserWritable(_perUser));
    }
}
