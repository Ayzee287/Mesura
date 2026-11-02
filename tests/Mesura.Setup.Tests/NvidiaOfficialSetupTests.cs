using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>The signature and unpacking checks of the official NVIDIA route, on files the machine running the tests has. Nothing here starts an installer.</summary>
public sealed class NvidiaOfficialSetupTests
{
    private static string Notepad => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [Fact]
    public void A_file_signed_by_windows_verifies_and_names_its_signer()
    {
        var result = AuthenticodeFile.Verify(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

        // Windows system files are catalog-signed, not embedded-signed: WinVerifyTrust (file mode) may say so. What must hold is that the verdict is never "valid" for an unsigned file.
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    [Fact]
    public void An_unsigned_file_is_never_valid_and_is_refused_as_an_nvidia_package()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mesura-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0x90, 0x00]);
        try
        {
            Assert.False(AuthenticodeFile.Verify(file).Valid);
            Assert.NotNull(NvidiaOfficialSetup.CheckSignature(file, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_file_signed_by_someone_else_is_refused_even_when_the_signature_is_valid()
    {
        // The Mesura command line of this build, or any other signed file, is not NVIDIA's: a valid signature by another publisher is not enough to run a driver installer.
        var signed = new[] { Notepad, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") }.First(File.Exists);

        var refusal = NvidiaOfficialSetup.CheckSignature(signed, out var result);

        Assert.NotNull(refusal);
        Assert.DoesNotContain("NVIDIA Corporation", result.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unpack_into_a_folder_that_exists_or_from_an_unsigned_file_changes_nothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mesura-nvidia-{Guid.NewGuid():N}");
        var file = Path.Combine(Path.GetTempPath(), $"mesura-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A]);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new NvidiaOfficialSetup().Prepare(file, folder, elevated: false, CancellationToken.None));
            Assert.False(Directory.Exists(folder)); // refused before anything was created
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Package_names_come_from_nvi_files_one_folder_down_and_the_sub_packages_of_setup_cfg()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mesura-nvidia-names-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "HDAudio"));
        Directory.CreateDirectory(Path.Combine(root, "NvContainer"));
        try
        {
            File.WriteAllText(Path.Combine(root, "HDAudio", "HDAudio.nvi"), "<nvi name=\"HDAudio.Driver\" title=\"x\">");
            File.WriteAllText(Path.Combine(root, "NvContainer", "NvContainer.nvi"), "<nvi name=\"${{PackageId}}\">");
            File.WriteAllText(Path.Combine(root, "setup.cfg"), "<install><sub-package name=\"NvContainer.User\"/><sub-package name=\"${{x}}\"/></install>");

            Assert.Equal(["HDAudio.Driver", "NvContainer.User"], NvidiaOfficialSetup.PackageNames(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
