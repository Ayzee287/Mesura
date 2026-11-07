using Mesura.Core.Update;

namespace Mesura.Core.Tests.Update;

public sealed class ReleaseNotesTests
{
    private const string Json = """
        {
          "0.6": { "en": ["Six."], "ru": ["Шесть."] },
          "0.7": { "en": ["One.", "Two."], "ru": ["Раз.", "Два."] },
          "0.8": { "en": ["Eight."] }
        }
        """;

    [Fact]
    public void A_patch_version_shows_the_notes_of_its_minor_version_in_its_language()
    {
        var notes = ReleaseNotes.Parse(Json);

        Assert.Equal(["One.", "Two."], notes.For("0.7.0", "en"));
        Assert.Equal(["One.", "Two."], notes.For("0.7.3", "en"));
        Assert.Equal(["Раз.", "Два."], notes.For("0.7.0", "ru"));
        Assert.Equal(["Six."], notes.For("0.6.0+abc123", "en"));
    }

    [Fact]
    public void A_language_without_notes_falls_back_to_english_and_an_unknown_version_has_none()
    {
        var notes = ReleaseNotes.Parse(Json);

        Assert.Equal(["Eight."], notes.For("0.8.1", "ru"));
        Assert.Empty(notes.For("0.9.0", "en"));
        Assert.Empty(notes.For("garbage", "en"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{ \"0.7\": 5 }")]
    public void Damaged_or_empty_text_gives_no_notes_and_no_exception(string? text)
    {
        Assert.Empty(ReleaseNotes.Parse(text).For("0.7.0", "en"));
    }

    [Fact]
    public void A_byte_order_mark_and_comments_are_tolerated()
    {
        var notes = ReleaseNotes.Parse("﻿{ /* the notes */ \"0.7\": { \"en\": [\"One.\"], }, }");

        Assert.Equal(["One."], notes.For("0.7.0", "en"));
    }
}
