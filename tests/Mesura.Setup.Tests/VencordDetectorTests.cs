using Mesura.Core.Apps;
using Mesura.Setup.Windows.Apps;

namespace Mesura.Setup.Tests;

/// <summary>
/// Vencord is read from Discord's own files: the newest <c>app-*</c> folder must carry the pack's loader. The layouts here are the real ones: the Windows Sandbox run of 2026-11-06 listed
/// <c>bootstrap, app.asar, build_info.json, _app.asar</c> after the Vencord installer ran. A Discord update makes a newer folder without the patch, which must read as "not installed".
/// </summary>
public sealed class VencordDetectorTests : IDisposable
{
    private readonly string _local = Directory.CreateTempSubdirectory("mesura-vencord-detect-").FullName;

    public void Dispose() => Directory.Delete(_local, recursive: true);

    private string Folder(string version, bool patched, bool loaderAsFolder = false, string loaderText = "require(\"C:/Users/x/AppData/Roaming/Vencord/dist/patcher.js\") // vencord")
    {
        var resources = Path.Combine(_local, "Discord", "app-" + version, "resources");
        Directory.CreateDirectory(resources);
        if (!patched)
        {
            File.WriteAllText(Path.Combine(resources, "app.asar"), "discord's own archive");
            return resources;
        }

        File.WriteAllText(Path.Combine(resources, "_app.asar"), "discord's own archive, kept by the patch");
        if (loaderAsFolder)
        {
            Directory.CreateDirectory(Path.Combine(resources, "app.asar"));
            File.WriteAllText(Path.Combine(resources, "app.asar", "index.js"), loaderText);
        }
        else
        {
            File.WriteAllText(Path.Combine(resources, "app.asar"), loaderText);
        }

        return resources;
    }

    [Fact]
    public void No_discord_at_all_is_not_installed()
    {
        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));
    }

    [Fact]
    public void Discord_without_any_version_folder_is_not_installed()
    {
        Directory.CreateDirectory(Path.Combine(_local, "Discord"));
        File.WriteAllText(Path.Combine(_local, "Discord", "Update.exe"), "stub");

        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));
    }

    [Fact]
    public void An_unpatched_discord_is_not_installed_and_a_patched_one_is_installed()
    {
        Folder("1.0.9267", patched: false);
        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));

        Folder("1.0.9267", patched: true);
        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_local));
    }

    [Fact]
    public void The_older_loader_that_is_a_folder_counts_too()
    {
        Folder("1.0.9100", patched: true, loaderAsFolder: true);

        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_local));
    }

    [Fact]
    public void A_discord_update_makes_a_newer_unpatched_folder_and_vencord_reads_as_gone_until_it_is_patched_again()
    {
        Folder("1.0.9267", patched: true);
        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_local));

        Folder("1.0.9300", patched: false); // what Discord's own updater does: a new folder, the old one stays for a while
        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));

        Folder("1.0.9300", patched: true); // the Vencord installer run again on the newest folder
        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_local));
    }

    [Fact]
    public void Versions_are_compared_as_numbers_not_as_text()
    {
        Folder("1.0.999", patched: true);
        Folder("1.0.1000", patched: false); // newer by number, older by text

        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));
    }

    [Fact]
    public void A_loader_that_does_not_name_vencord_is_not_vencord()
    {
        Folder("1.0.9267", patched: true, loaderText: "require('./other-mod.js')");

        Assert.Equal(InstallState.NotInstalled, VencordDetector.Detect(_local));
    }

    [Fact]
    public void A_folder_that_is_not_a_version_is_ignored()
    {
        Folder("1.0.9267", patched: true);
        Directory.CreateDirectory(Path.Combine(_local, "Discord", "app-backup"));

        Assert.Equal(InstallState.Installed, VencordDetector.Detect(_local));
    }
}
