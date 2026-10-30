using System.IO;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

/// <summary>
/// Entries the 0.4 audit took out of the product catalog but the engine tests still need as fixtures (a per-user registry write, a service,
/// a scheduled task, operations that set one value two ways): <c>Fixtures/engine-fixtures.catalog.json</c>. <see cref="Op"/> looks in the
/// product catalog first, then in the fixtures.
/// </summary>
internal static class EngineFixtures
{
    private static readonly Lazy<WindowsCatalog> Fixtures = new(() =>
        WindowsCatalog.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "engine-fixtures.catalog.json"))));

    public static WindowsOperation Op(string id) =>
        WindowsCatalog.Default.ById(id) ?? Fixtures.Value.ById(id) ?? throw new InvalidOperationException("No such entry in the catalog or the fixtures: " + id);
}
