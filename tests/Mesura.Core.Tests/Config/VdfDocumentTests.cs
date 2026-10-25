using Mesura.Core.Config;

namespace Mesura.Core.Tests.Config;

/// <summary>
/// The import contract for VDF: edit only named keys, preserve everything else, and stay idempotent (re-running an
/// import changes nothing and adds no duplicate keys).
/// </summary>
public sealed class VdfDocumentTests
{
    private const string Sample = """
        "UserLocalConfigStore"
        {
        	"system"
        	{
        		"EnableGameOverlay"		"1"
        		"PushToTalkKey"		"0"
        	}
        	"friends"
        	{
        		"PersonaName"		"someone"
        	}
        	"UnknownFutureSection"
        	{
        		"KeepMe"		"42"
        	}
        }
        """;

    [Fact]
    public void Round_trip_parses_and_rewrites_every_key()
    {
        var doc = VdfDocument.Parse(Sample);
        var text = doc.ToText();
        var reparsed = VdfDocument.Parse(text);

        Assert.Equal("1", reparsed.GetValue("UserLocalConfigStore/system/EnableGameOverlay"));
        Assert.Equal("someone", reparsed.GetValue("UserLocalConfigStore/friends/PersonaName"));
        Assert.Equal("42", reparsed.GetValue("UserLocalConfigStore/UnknownFutureSection/KeepMe"));
    }

    [Fact]
    public void SetValue_replaces_in_place_and_leaves_unknown_keys_untouched()
    {
        var doc = VdfDocument.Parse(Sample);

        Assert.True(doc.SetValue("UserLocalConfigStore/system/EnableGameOverlay", "0"));

        Assert.Equal("0", doc.GetValue("UserLocalConfigStore/system/EnableGameOverlay"));
        // Unrelated keys and the whole unknown section survive.
        Assert.Equal("0", doc.GetValue("UserLocalConfigStore/system/PushToTalkKey"));
        Assert.Equal("someone", doc.GetValue("UserLocalConfigStore/friends/PersonaName"));
        Assert.Equal("42", doc.GetValue("UserLocalConfigStore/UnknownFutureSection/KeepMe"));
    }

    [Fact]
    public void SetValue_twice_is_idempotent_and_adds_no_duplicate()
    {
        var doc = VdfDocument.Parse(Sample);
        doc.SetValue("UserLocalConfigStore/system/EnableGameOverlay", "0");
        var once = doc.ToText();
        doc.SetValue("UserLocalConfigStore/system/EnableGameOverlay", "0");
        var twice = doc.ToText();

        Assert.Equal(once, twice);
        var system = doc.Root("UserLocalConfigStore")!.Child("system")!;
        Assert.Single(system.Children, c => c.Key == "EnableGameOverlay");
    }

    [Fact]
    public void SetValue_creates_missing_intermediate_blocks()
    {
        var doc = VdfDocument.Parse(Sample);

        Assert.True(doc.SetValue("UserLocalConfigStore/news/NotifyAvailableGames", "0"));

        Assert.Equal("0", doc.GetValue("UserLocalConfigStore/news/NotifyAvailableGames"));
        // Existing siblings are preserved.
        Assert.Equal("1", doc.GetValue("UserLocalConfigStore/system/EnableGameOverlay"));
    }

    [Fact]
    public void SetValue_refuses_to_overwrite_a_block_with_a_value()
    {
        var doc = VdfDocument.Parse(Sample);

        Assert.False(doc.SetValue("UserLocalConfigStore/system", "oops"));
        Assert.NotNull(doc.Root("UserLocalConfigStore")!.Child("system"));
        Assert.True(doc.Root("UserLocalConfigStore")!.Child("system")!.IsBlock);
    }

    [Fact]
    public void GetValue_is_null_for_a_missing_path_or_a_block()
    {
        var doc = VdfDocument.Parse(Sample);

        Assert.Null(doc.GetValue("UserLocalConfigStore/system/DoesNotExist"));
        Assert.Null(doc.GetValue("UserLocalConfigStore/system")); // a block, not a value
        Assert.Null(doc.GetValue("Missing/key"));
    }

    [Fact]
    public void Keys_match_case_insensitively_as_steam_reads_them()
    {
        var doc = VdfDocument.Parse(Sample);
        Assert.Equal("1", doc.GetValue("userlocalconfigstore/SYSTEM/enablegameoverlay"));
    }

    [Fact]
    public void Escapes_round_trip()
    {
        const string vdf = """
            "root"
            {
            	"path"		"C:\\Games\\Steam"
            	"quote"		"a \"quoted\" word"
            }
            """;
        var doc = VdfDocument.Parse(vdf);
        Assert.Equal(@"C:\Games\Steam", doc.GetValue("root/path"));
        Assert.Equal("a \"quoted\" word", doc.GetValue("root/quote"));

        var reparsed = VdfDocument.Parse(doc.ToText());
        Assert.Equal(@"C:\Games\Steam", reparsed.GetValue("root/path"));
        Assert.Equal("a \"quoted\" word", reparsed.GetValue("root/quote"));
    }

    [Fact]
    public void Deeply_nested_input_is_refused_not_a_stack_overflow()
    {
        var deep = string.Concat(Enumerable.Repeat("\"a\"{", VdfDocument.MaxDepth + 2));
        Assert.Throws<FormatException>(() => VdfDocument.Parse(deep));
    }

    [Fact]
    public void Unclosed_block_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => VdfDocument.Parse("\"root\"\n{\n\t\"k\"\t\"v\"\n"));
    }

    [Fact]
    public void A_value_with_raw_newlines_round_trips_byte_for_byte()
    {
        // Steam's config.vdf stores controller mappings as one quoted value spanning lines, and paths with escaped
        // backslashes; both must come back exactly as Steam wrote them.
        const string text = "\"root\"\n{\n\t\"map\"\t\t\"a,platform:Windows,\n03000000,Pad\"\n\t\"path\"\t\t\"C:\\\\Games\"\n}\n";
        Assert.Equal(text, VdfDocument.Parse(text).ToText());
    }
}
