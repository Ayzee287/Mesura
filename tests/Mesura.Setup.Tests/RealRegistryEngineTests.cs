using Mesura.Core.Windows;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows.Windows;
using Microsoft.Win32;

namespace Mesura.Setup.Tests;

/// <summary>
/// The configuration engine against the real registry, in a throwaway key under the signed-in user's own hive
/// (<c>HKCU\Software\MesuraTest-...</c>, deleted afterwards): the real reader and writer for binary values and for settings
/// applied to every matching subkey, which the in-memory tests can't prove. Nothing else on the PC is touched. Skipped where
/// the signed-in user can't be resolved.
/// </summary>
public sealed class RealRegistryEngineTests
{
    [Fact]
    public void Binary_and_per_subkey_settings_are_written_read_back_and_reverted_in_the_real_registry()
    {
        var reader = new SystemStateReader();
        if (!reader.User.Resolved)
        {
            return; // a session without a resolved user (a service account): nothing safe to write
        }

        var root = $@"Software\MesuraTest-{Guid.NewGuid():N}";
        var writer = new WindowsWriter(reader.User);
        try
        {
            writer.SetRegistry("HKCU", $@"{root}\Adapters\A\Declares", "v", RegistryKind.String, "1");
            writer.SetRegistry("HKCU", $@"{root}\Adapters\B\Other", "v", RegistryKind.String, "1");
            writer.SetRegistry("HKCU", root, "Mask", RegistryKind.Binary, "9e,3e,07,80");

            var escaped = root.Replace("\\", "\\\\", StringComparison.Ordinal);
            var catalog = WindowsCatalog.Parse($$"""
                { "kind": "Mesura.WindowsCatalog", "schemaVersion": 4, "presets": [], "operations": [ {
                  "id": "test.real", "name": "Real", "category": "System", "description": "d", "risk": "userPreference",
                  "registry": [ { "hive": "HKCU", "path": "{{escaped}}", "name": "Mask", "type": "binary", "value": "90,12,03,80" } ],
                  "registryEach": [ { "hive": "HKCU", "pathPattern": "{{escaped}}\\Adapters\\*", "name": "Flag", "type": "dword", "value": "1",
                                      "where": { "subkeyExists": "Declares" } } ] } ] }
                """);
            var op = catalog.Operations.Single();
            var configurator = new WindowsConfigurator(reader, writer);

            Assert.Equal(OperationState.NotApplied, WindowsEvaluator.Evaluate(op, reader).State);
            var outcome = configurator.Apply([op], TestContext.Current.CancellationToken).Single();

            Assert.Equal(WindowsOpStatus.Applied, outcome.Status);
            Assert.Equal(2, outcome.Changes.Count); // the mask, and the flag on A only
            Assert.Equal("90,12,03,80", reader.ReadRegistry("HKCU", root, "Mask").Value);
            Assert.Equal(RegistryKind.Binary, reader.ReadRegistry("HKCU", root, "Mask").Kind);
            Assert.Equal("1", reader.ReadRegistry("HKCU", $@"{root}\Adapters\A", "Flag").Value);
            Assert.False(reader.ReadRegistry("HKCU", $@"{root}\Adapters\B", "Flag").ValueExists);
            Assert.Equal(OperationState.Applied, WindowsEvaluator.Evaluate(op, reader).State);

            var reverted = configurator.Revert([outcome]).Single();

            Assert.Equal(WindowsOpStatus.Reverted, reverted.Status);
            Assert.Equal("9e,3e,07,80", reader.ReadRegistry("HKCU", root, "Mask").Value);
            Assert.False(reader.ReadRegistry("HKCU", $@"{root}\Adapters\A", "Flag").ValueExists);
        }
        finally
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            using var hive = users.OpenSubKey(reader.User.Sid!, writable: true);
            hive?.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }
}
