using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mesura.Core.Privacy;
using Mesura.Core.Raw;
using Mesura.Core.Raw.Serialization;

namespace Mesura.Core.Tests;

/// <summary>
/// Enforces the policy for scrubbed real-machine fixtures in git (CLAUDE.md, architecture §13): each one is
/// listed in the folder's manifest with its privacy review, reads under the current schema, was captured from a
/// clean commit, and holds nothing the scrub, the stand-ins or the Steam redaction should have removed. The
/// automated part can't prove a fixture safe on its own; the manifest records the manual review too.
/// </summary>
public sealed partial class ScrubbedFixtureTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrubbed");

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void The_folder_holds_only_listed_fixtures_and_the_manifest()
    {
        var manifest = File.ReadAllText(Path.Combine(Folder, "README.md"));

        Assert.All(Directory.EnumerateFiles(Folder), file =>
        {
            var name = Path.GetFileName(file);
            Assert.True(name == "README.md" || (name.EndsWith(".json", StringComparison.Ordinal) && manifest.Contains($"`{name}`", StringComparison.Ordinal)), $"{name} is not a listed fixture");
        });
    }

    [Theory(SkipTestWithoutData = true)] // the folder may hold no fixture yet
    [MemberData(nameof(Fixtures))]
    public void A_fixture_reads_under_the_current_schema_and_names_the_commit_that_collected_it(string name)
    {
        var snapshot = Read(name);

        Assert.True(snapshot.App.Commit.IsKnown, $"{name} was captured from a build with uncommitted changes");
        Assert.Matches("^PCB?-[0-9]{3}-[0-9]{8}T[0-9]{6}Z\\.json$", name);
    }

    [Theory(SkipTestWithoutData = true)] // the folder may hold no fixture yet
    [MemberData(nameof(Fixtures))]
    public void A_fixture_has_nothing_left_for_the_edid_and_smbios_scrubs_to_remove(string name)
    {
        var sources = Read(name).Sources;

        Assert.All(
            sources.Edid.Data?.Monitors.Where(monitor => monitor.Edid.IsKnown) ?? [],
            monitor => Assert.Equal(0, EdidScrubber.Scrub(monitor.Edid.Value!.Bytes).FieldsScrubbed));

        if (sources.Smbios.Data is { } smbios)
        {
            var again = SmbiosScrubber.Scrub(smbios.Table);
            Assert.Equal(0, again.FieldsScrubbed);
            Assert.Equal(smbios.Table, again.Table);
        }
    }

    [Theory(SkipTestWithoutData = true)] // the folder may hold no fixture yet
    [MemberData(nameof(Fixtures))]
    public void A_fixture_holds_stand_ins_not_opaque_platform_ids(string name)
    {
        var json = File.ReadAllText(Path.Combine(Folder, name));

        Assert.All(Values(json, "adapterId"), id => Assert.Matches("^adapter-[0-9]+$", id));
        Assert.All(Values(json, "instanceId"), id => Assert.Matches(@"\\instance-[0-9]+$", id));
        Assert.All(Values(json, "monitorDevicePath"), path => Assert.Matches("^[^#]*#[^#]*#instance-[0-9]+#", path));
        Assert.DoesNotMatch(RealVolumeGuid(), json);
    }

    [Theory(SkipTestWithoutData = true)] // the folder may hold no fixture yet
    [MemberData(nameof(Fixtures))]
    public void A_fixture_holds_no_user_profile_path_and_no_personal_steam_value(string name)
    {
        var strings = AllStrings(File.ReadAllText(Path.Combine(Folder, name))).ToList();

        Assert.DoesNotContain(strings, text => UserFolder().IsMatch(text));
        Assert.All(
            strings.SelectMany(text => SteamPersonalValue().Matches(text)),
            match => Assert.Contains(match.Groups["value"].Value, new[] { "", SteamTextScrubber.Redacted }));
    }

    private static RawSnapshot Read(string name) => SnapshotJson.Deserialize(File.ReadAllBytes(Path.Combine(Folder, name)));

    private static IEnumerable<string> Values(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return Walk(document.RootElement)
            .Where(pair => pair.Property == property && pair.Value.ValueKind == JsonValueKind.String)
            .Select(pair => pair.Value.GetString()!)
            .ToList();
    }

    private static IEnumerable<string> AllStrings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Walk(document.RootElement)
            .Where(pair => pair.Value.ValueKind == JsonValueKind.String && pair.Property is not ("bytes" or "table"))
            .Select(pair => pair.Value.GetString()!)
            .ToList();
    }

    private static IEnumerable<(string? Property, JsonElement Value)> Walk(JsonElement element, string? property = null)
    {
        yield return (property, element);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject())
                {
                    // A reading's "value" belongs to the property that holds the reading.
                    foreach (var pair in Walk(child.Value, child.Name == "value" ? property : child.Name))
                    {
                        yield return pair;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    foreach (var pair in Walk(child, property))
                    {
                        yield return pair;
                    }
                }

                break;
        }
    }

    /// <summary>A real volume GUID (hex digits), as opposed to a <c>{volume-N}</c> stand-in.</summary>
    [GeneratedRegex(@"Volume\{[0-9A-Fa-f]{8}-", RegexOptions.CultureInvariant)]
    private static partial Regex RealVolumeGuid();

    /// <summary>A path into a user's folder that the profile redaction should have rewritten.</summary>
    [GeneratedRegex(@"[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[^\\/%""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"""(?:LastOwner|LastPlayed|contentid|contentstatsid|label)""[ \t]+""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamPersonalValue();
}
