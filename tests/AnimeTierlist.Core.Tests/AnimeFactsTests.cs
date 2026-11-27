using System.Collections.Immutable;
using AnimeTierlist.Core;

namespace AnimeTierlist.Core.Tests;

/// <summary>Filterable facts derived from LiveChart's card and profile text and from cached videos.</summary>
public sealed class AnimeFactsTests
{
    private static readonly DateTimeOffset Retrieved = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("TV · Light Novel · 12 eps × 24m · Finished", "TV", "Light Novel")]
    [InlineData("Movie · Original · 1h 50m", "Movie", "Original")]
    [InlineData("ONA · Web Manga", "ONA", "Web Manga")]
    [InlineData("TV · Music · 12 eps", "TV", "Music")]
    [InlineData("Manga · 12 eps × 24m", null, "Manga")]
    [InlineData("TV · 12 eps × 24m · Releasing", "TV", null)]
    [InlineData("Music", "Music", null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Metadata_yields_format_and_source_material(string? metadata, string? format, string? source)
    {
        Assert.Equal((format, source), AnimeFacts.ParseMetadata(metadata));
    }

    [Theory]
    [InlineData("8.12 out of 10 based on 1,204 user ratings", 8.12)]
    [InlineData("7.5 (Good)", 7.5)]
    [InlineData("9", 9.0)]
    [InlineData("6,75", 6.75)]
    public void Leading_score_is_the_rating(string text, double expected)
    {
        Assert.Equal(expected, AnimeFacts.ParseRating(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("–")]
    [InlineData("No ratings yet")]
    [InlineData("11.2 out of 10")]
    public void Missing_or_out_of_range_scores_are_unrated(string? text)
    {
        Assert.Null(AnimeFacts.ParseRating(text));
    }

    [Fact]
    public void Profile_values_win_and_the_card_fills_gaps()
    {
        var card = new SourceDetails(rating: "6.10 out of 10 based on 30 user ratings", metadata: "Manga · 12 eps", studios: ["Card Studio"], tags: ["Comedy"]);
        var profile = new SourceDetails(rating: "8.40 (Great)", metadata: "TV · 12 eps × 24m", tags: ["Action", "Fantasy"]);
        var extras = new AnimeExtraDetails(TestData.A, Retrieved, profile, [], videosFetched: false, profileFetched: true);

        var facts = AnimeFacts.From(card, extras);

        Assert.Equal("TV", facts.Format);
        Assert.Equal("Manga", facts.SourceMaterial); // The profile lists no source here, so the card's applies.
        Assert.Equal(8.4, facts.Rating);
        Assert.Equal(["Action", "Fantasy"], facts.Genres);
        Assert.Equal(["Card Studio"], facts.Studios);
        Assert.Null(facts.HasOpening);
    }

    [Fact]
    public void Opening_is_known_only_after_videos_were_fetched()
    {
        var op = new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=aaaaaaaaaaa"), AnimeVideoKind.Opening);
        var ed = new AnimeVideo("ED", new Uri("https://www.youtube.com/watch?v=bbbbbbbbbbb"), AnimeVideoKind.Ending);

        Assert.Null(AnimeFacts.From(SourceDetails.Empty, null).HasOpening);
        Assert.True(AnimeFacts.From(null, new AnimeExtraDetails(TestData.A, Retrieved, SourceDetails.Empty, [ed, op], profileFetched: false)).HasOpening);
        Assert.False(AnimeFacts.From(null, new AnimeExtraDetails(TestData.A, Retrieved, SourceDetails.Empty, [ed], profileFetched: false)).HasOpening);
        Assert.False(AnimeFacts.From(null, new AnimeExtraDetails(TestData.A, Retrieved, SourceDetails.Empty, ImmutableArray<AnimeVideo>.Empty, profileFetched: false)).HasOpening);
    }

    [Fact]
    public void Show_without_any_details_has_unknown_facts()
    {
        Assert.Same(AnimeFacts.Unknown, AnimeFacts.From(SourceDetails.Empty, null));
        Assert.Same(AnimeFacts.Unknown, AnimeFacts.From(null, null));
    }
}
