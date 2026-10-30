using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>
/// An elevated Mesura starts NVIDIA's tools with its own token. A per-user winget package sits where any program the user runs can replace
/// the executable, so it is refused when elevated and allowed otherwise (docs/security.md).
/// </summary>
public sealed class NvidiaToolTrustTests : IDisposable
{
    private readonly string _perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MesuraToolTest-" + Guid.NewGuid().ToString("N")[..8]);

    public NvidiaToolTrustTests() => Directory.CreateDirectory(_perUser);

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

    [Fact]
    public void An_elevated_Mesura_refuses_a_tool_in_a_user_writable_folder_and_says_what_to_do()
    {
        var refusal = NvidiaTools.Refusal("NVIDIA Profile Inspector", Path.Combine(_perUser, "nvidiaProfileInspector.exe"), elevated: true);

        Assert.NotNull(refusal);
        Assert.Contains("administrator rights", refusal, StringComparison.Ordinal);
        Assert.Contains("--scope machine", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_elevation_the_same_tool_runs_with_the_users_own_rights()
    {
        Assert.Null(NvidiaTools.Refusal("NVIDIA Profile Inspector", Path.Combine(_perUser, "nvidiaProfileInspector.exe"), elevated: false));
    }

    [Fact]
    public void A_tool_in_a_protected_folder_is_allowed_even_elevated()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "tool.exe"); // the folder exists; the check reads its access list

        Assert.Null(NvidiaTools.Refusal("Tool", exe, elevated: true));
    }
}
