using FpsUtility.Core.Raw;
using FpsUtility.Core.Support;

namespace FpsUtility.Core.Tests;

/// <summary>The startup platform check (D008, D011, D013), decided without a real OS.</summary>
public sealed class PlatformSupportTests
{
    [Theory]
    [InlineData(19045)] // Windows 10 22H2, the floor
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(26200)] // Windows 11 25H2
    public void Supported_client_builds_on_x64_are_accepted(int build)
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(build));

        Assert.True(decision.IsSupported);
        Assert.Empty(decision.Refusals);
        Assert.Null(decision.Message);
    }

    [Theory]
    [InlineData(19044)] // Windows 10 21H2 and LTSC 2021: one build below the floor
    [InlineData(17763)]
    [InlineData(10240)]
    public void Builds_below_19045_are_refused(int build)
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(build));

        Assert.False(decision.IsSupported);
        Assert.Equal([PlatformRefusal.BuildBelowFloor], decision.Refusals);
    }

    [Theory]
    [InlineData(MachineArchitecture.Arm64)] // Windows 11 on Arm running this x64 app under emulation
    [InlineData(MachineArchitecture.X86)]
    [InlineData(MachineArchitecture.Other)]
    public void Non_x64_is_refused(MachineArchitecture architecture)
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(architecture: architecture));

        Assert.Equal([PlatformRefusal.NotX64], decision.Refusals);
    }

    [Theory]
    [InlineData(20348)] // Windows Server 2022
    [InlineData(26100)] // Windows Server 2025, above the client floor
    public void Windows_server_is_refused_whatever_its_build(int build)
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(build, productType: OsProductType.Server));

        Assert.Equal([PlatformRefusal.Server], decision.Refusals);
        Assert.Contains("Windows Server isn't supported.", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_that_could_not_be_read_are_refusals_not_passes()
    {
        var os = SnapshotFixture.Os() with
        {
            Architecture = Reading.Missing<MachineArchitecture>(MissingReason.SourceFailed, "IsWow64Process2 failed: ERROR_ACCESS_DENIED (5)"),
            ProductType = Reading.Missing<OsProductType>(MissingReason.SourceFailed, "VerifyVersionInfoW(VER_PRODUCT_TYPE) failed: ERROR_INVALID_PARAMETER (87)"),
        };

        var decision = PlatformSupport.Evaluate(os);

        Assert.Equal([PlatformRefusal.ArchitectureUnknown, PlatformRefusal.ProductTypeUnknown], decision.Refusals);
        Assert.Contains("IsWow64Process2 failed", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reason_is_reported_at_once()
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(17763, MachineArchitecture.Arm64, OsProductType.Server));

        Assert.Equal([PlatformRefusal.BuildBelowFloor, PlatformRefusal.NotX64, PlatformRefusal.Server], decision.Refusals);
    }

    [Fact]
    public void The_refusal_message_names_what_was_detected_and_what_is_required()
    {
        var decision = PlatformSupport.Evaluate(SnapshotFixture.Os(19044, MachineArchitecture.Arm64));

        Assert.StartsWith("This version of Windows isn't supported", decision.Message, StringComparison.Ordinal);
        Assert.Contains("edition Professional", decision.Message, StringComparison.Ordinal);
        Assert.Contains("version 22H2", decision.Message, StringComparison.Ordinal);
        Assert.Contains("build 19044.6456", decision.Message, StringComparison.Ordinal);
        Assert.Contains("architecture Arm64", decision.Message, StringComparison.Ordinal);
        Assert.EndsWith(PlatformSupport.Requirement, decision.Message, StringComparison.Ordinal);
        Assert.Contains("x64", PlatformSupport.Requirement, StringComparison.Ordinal); // "64-bit" alone would include Arm64
    }
}
