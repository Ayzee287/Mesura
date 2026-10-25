using Mesura.Core.Apps;

namespace Mesura.Core.Tests.Apps;

public sealed class AppCatalogTests
{
    private static string Catalog(string apps, string bundles = "[]") =>
        "{ \"kind\": \"Mesura.AppCatalog\", \"schemaVersion\": 1, \"apps\": " + apps + ", \"bundles\": " + bundles + " }";

    [Fact]
    public void The_built_in_catalog_loads_and_every_bundle_resolves_fully()
    {
        var catalog = AppCatalog.Default;

        Assert.NotEmpty(catalog.Packages);
        Assert.All(catalog.Packages, p => Assert.False(string.IsNullOrWhiteSpace(p.WingetId)));
        Assert.All(catalog.Bundles, b => Assert.Equal(b.AppIds.Count, catalog.Resolve(b).Count));
    }

    [Fact]
    public void Defaults_apply_for_omitted_fields()
    {
        var app = AppCatalog.Parse(Catalog("""[ { "id": "a", "wingetId": "A.A", "name": "A" } ]""")).Packages.Single();

        Assert.Equal(AppSource.Winget, app.Source);
        Assert.True(app.Detectable);
        Assert.Equal("Other", app.Category);
    }

    [Theory]
    [InlineData("""[ { "id": "a", "wingetId": "A.A", "name": "A" }, { "id": "A", "wingetId": "B.B", "name": "B" } ]""", "[]", "more than once")]
    [InlineData("""[ { "id": "a", "name": "A" } ]""", "[]", "wingetId")]
    [InlineData("""[ { "id": "a", "name": "A", "source": "manual" } ]""", "[]", "url")]
    [InlineData("""[ { "id": "a", "wingetId": "A.A", "name": "A", "source": "choco" } ]""", "[]", "unknown source")]
    [InlineData("""[ { "id": "a", "wingetId": "A.A", "name": "A" } ]""", """[ { "id": "b", "apps": [ "a", "zz" ] } ]""", "'zz'")]
    public void Inconsistent_catalogs_are_rejected_with_a_pointed_message(string apps, string bundles, string expected)
    {
        var e = Assert.Throws<FormatException>(() => AppCatalog.Parse(Catalog(apps, bundles)));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_schema_or_another_file_kind_is_rejected()
    {
        Assert.Throws<FormatException>(() => AppCatalog.Parse("""{ "kind": "Mesura.AppCatalog", "schemaVersion": 99 }"""));
        Assert.Throws<FormatException>(() => AppCatalog.Parse("""{ "kind": "Mesura.ConfigBundle", "schemaVersion": 1 }"""));
    }
}
