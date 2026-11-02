using System.Security.Cryptography;
using Mesura.Setup.Windows.Nvidia;

namespace Mesura.Setup.Tests;

/// <summary>
/// The check that lets an unlisted NVIDIA Profile Inspector build be trusted without anyone adding a hash, against the real GitHub release page. It needs a genuine release on disk
/// (MESURA_NVPI_SAMPLE, a folder holding nvidiaProfileInspector.exe of release 3.0.2.1) and a connection, so it skips without them.
/// </summary>
public sealed class GitHubReleaseVerifierTests
{
    private static string? Sample()
    {
        var folder = Environment.GetEnvironmentVariable("MESURA_NVPI_SAMPLE");
        return folder is not null && File.Exists(Path.Combine(folder, "nvidiaProfileInspector.exe")) ? Path.Combine(folder, "nvidiaProfileInspector.exe") : null;
    }

    [Fact]
    public void The_published_release_is_recognised_and_anything_else_is_not()
    {
        var exe = Sample();
        Assert.SkipUnless(exe is not null, "Set MESURA_NVPI_SAMPLE to a folder holding a genuine NVIDIA Profile Inspector 3.0.2.1.");
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe!)));
        var verifier = new GitHubReleaseVerifier();

        var genuine = verifier.Verify(new Version(3, 0, 2, 1), sha, TestContext.Current.CancellationToken, out var genuineReason);
        Assert.SkipUnless(genuine || !genuineReason.Contains("reached", StringComparison.Ordinal), "GitHub could not be reached from here.");

        Assert.True(genuine, genuineReason);
        Assert.False(verifier.Verify(new Version(3, 0, 2, 1), new string('0', 64), TestContext.Current.CancellationToken, out var swapped)); // a different file for the same version
        Assert.Contains("not the file published", swapped, StringComparison.Ordinal);
        Assert.False(verifier.Verify(new Version(9, 9, 9, 9), sha, TestContext.Current.CancellationToken, out var none)); // a version nobody published
        Assert.Contains("no published release", none, StringComparison.Ordinal);
    }
}
