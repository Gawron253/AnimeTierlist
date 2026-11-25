using System.Globalization;
using System.Text.Json;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.LiveChart;

namespace AnimeTierlist.Infrastructure.Tests.LiveChart;

/// <summary>
/// Against synthetic LiveChart-style season pages. Expected values come from the *.expected.json oracles, which
/// tests/fixtures/livechart/New-SyntheticFixtures.ps1 writes from its own show list, independently of this parser.
/// </summary>
public sealed class LiveChartSeasonParserTests
{
    private static readonly DateTimeOffset Retrieved = new(2026, 11, 17, 22, 57, 0, TimeSpan.Zero);

    public static TheoryData<string> CompleteFixtures => new() { "summer-2025-tv", "summer-2025-movies", "summer-2025-ovas", "summer-2025-all", "winter-2025-tv" };

    [Theory]
    [MemberData(nameof(CompleteFixtures))]
    public void Ordered_ids_equal_the_rendered_page_oracle_exactly(string fixture)
    {
        var (expected, result) = ParseFixture(fixture);

        var capture = AssertValid(result);
        var oracle = expected.GetProperty("oracle").GetProperty("orderedIds").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.NotEmpty(oracle);
        Assert.Equal(oracle, capture.Occurrences.Select(o => o.Item.Value));
        Assert.Equal(Enumerable.Range(0, oracle.Length), capture.Occurrences.Select(o => o.Ordinal));
        Assert.Equal(LiveChartSeasonParser.ParserVersion, capture.ParserVersion);
    }

    [Fact]
    public void Displayed_titles_equal_the_rendered_page_oracle()
    {
        var (expected, result) = ParseFixture("summer-2025-tv");

        var oracleTitles = expected.GetProperty("oracle").GetProperty("displayedTitles").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(oracleTitles, AssertValid(result).Occurrences.Select(o => o.Titles.Displayed));
    }

    [Fact]
    public void Alternate_languages_come_from_the_same_card_and_default_falls_back_to_romanized()
    {
        var (expected, result) = ParseFixture("summer-2025-tv");
        var capture = AssertValid(result);

        var example = expected.GetProperty("languageExample");
        var titles = capture.Occurrences.Single(o => o.Item.Value == example.GetProperty("id").GetInt32()).Titles;
        Assert.Equal(example.GetProperty("displayed").GetString(), titles.Displayed);
        Assert.Equal(example.GetProperty("english").GetString(), titles.English);
        Assert.Equal(example.GetProperty("romanized").GetString(), titles.Romanized);
        Assert.Equal(example.GetProperty("native").GetString(), titles.Native);

        var fallback = expected.GetProperty("romanizedFallbackExample");
        var noEnglish = capture.Occurrences.Single(o => o.Item.Value == fallback.GetProperty("id").GetInt32()).Titles;
        Assert.Null(noEnglish.English);
        Assert.Equal(fallback.GetProperty("displayed").GetString(), noEnglish.Displayed);
    }

    [Fact]
    public void Visible_ratings_are_not_the_popularity_order()
    {
        var (expected, result) = ParseFixture("summer-2025-tv");
        var capture = AssertValid(result);
        var html = File.ReadAllText(FixturePath("summer-2025-tv.html"));

        var note = expected.GetProperty("ratingIsNotOrder");
        Assert.Equal(note.GetProperty("position10Id").GetInt32(), capture.Occurrences[10].Item.Value);
        // The card at popularity position 10 shows a higher rating than position 0, so a rating sort would move it.
        var rating10 = double.Parse(note.GetProperty("position10Rating").GetString()!, CultureInfo.InvariantCulture);
        var rating0 = double.Parse(note.GetProperty("position0Rating").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(rating10 > rating0);
        Assert.Contains($"title=\"{note.GetProperty("position10Rating").GetString()} out of 10", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_candidates_are_source_published_variants_largest_first()
    {
        var (_, result) = ParseFixture("summer-2025-tv");

        var first = AssertValid(result).Occurrences[0];
        Assert.Equal(["large", "small"], first.ImageCandidates.Select(c => c.Variant));
        Assert.All(first.ImageCandidates, c => Assert.Equal("u.livechart.me", c.Url.Host));
        Assert.All(AssertValid(result).Occurrences, o => Assert.NotEmpty(o.ImageCandidates));
    }

    [Fact]
    public void All_category_contains_each_category_subsequence_in_order()
    {
        var all = AssertValid(ParseFixture("summer-2025-all").Result).Occurrences.Select(o => o.Item).ToList();
        foreach (var part in new[] { "summer-2025-tv", "summer-2025-movies", "summer-2025-ovas" })
        {
            var ids = AssertValid(ParseFixture(part).Result).Occurrences.Select(o => o.Item).ToList();
            var set = ids.ToHashSet();
            Assert.Equal(ids, all.Where(set.Contains));
        }
    }

    [Fact]
    public void Positively_verified_empty_season_is_valid_and_empty()
    {
        var (_, result) = ParseFixture("fall-2027-movies");

        var valid = Assert.IsType<SeasonPageResult.Valid>(result);
        Assert.True(valid.IsVerifiedEmpty);
    }

    [Fact]
    public void Changed_card_selector_is_not_mistaken_for_an_empty_season()
    {
        var html = Fixture("summer-2025-movies").Replace("data-anime-id=", "data-id=", StringComparison.Ordinal);

        var result = Parse(html, Season.Summer, CatalogueCategory.Movies);

        Assert.Equal(SeasonPageFailure.InvalidCard, AssertInvalid(result).Failure);
    }

    [Fact]
    public void Unrecognized_card_markup_fails()
    {
        var html = Fixture("summer-2025-movies")
            .Replace("data-anime-id=", "data-id=", StringComparison.Ordinal)
            .Replace("lc-anime-details-card", "lc-show-card", StringComparison.Ordinal)
            .Replace("data-controller=\"anime-item\"", "data-controller=\"show\"", StringComparison.Ordinal);

        Assert.Equal(SeasonPageFailure.ChangedLayout, AssertInvalid(Parse(html, Season.Summer, CatalogueCategory.Movies)).Failure);
    }

    [Fact]
    public void Missing_chart_container_fails()
    {
        var html = Fixture("fall-2027-movies").Replace("seasonal-chart", "something-else", StringComparison.Ordinal);

        Assert.Equal(SeasonPageFailure.ChangedLayout, AssertInvalid(Parse(html, Season.Fall, CatalogueCategory.Movies, 2027)).Failure);
    }

    [Theory]
    [InlineData("Sort: Popularity", "Sort: Rating")]
    [InlineData("<span class=\"lcm3-button__label shrink text-center truncate\">Sort: Popularity</span>", "")]
    public void Non_popularity_sort_context_fails(string from, string to)
    {
        var html = Fixture("summer-2025-movies").Replace(from, to, StringComparison.Ordinal);

        Assert.Equal(SeasonPageFailure.WrongSortContext, AssertInvalid(Parse(html, Season.Summer, CatalogueCategory.Movies)).Failure);
    }

    [Fact]
    public void Page_for_a_different_route_fails()
    {
        var result = Parse(Fixture("summer-2025-movies"), Season.Summer, CatalogueCategory.Tv);

        Assert.Equal(SeasonPageFailure.WrongPage, AssertInvalid(result).Failure);
    }

    [Fact]
    public void Not_found_page_fails_rather_than_reporting_empty()
    {
        const string notFound = "<!DOCTYPE html><html><head><title>Page Not Found | LiveChart.me</title></head><body><main>404 - Not Found</main></body></html>";

        Assert.Equal(SeasonPageFailure.WrongPage, AssertInvalid(Parse(notFound, Season.Winter, CatalogueCategory.Ovas, 2030)).Failure);
    }

    [Fact]
    public void Card_without_displayed_title_fails()
    {
        var html = Fixture("summer-2025-movies").Replace("data-anime-item-target=\"preferredTitle\"", "data-anime-item-target=\"title\"", StringComparison.Ordinal);

        Assert.Equal(SeasonPageFailure.InvalidCard, AssertInvalid(Parse(html, Season.Summer, CatalogueCategory.Movies)).Failure);
    }

    [Fact]
    public void Optional_livechart_details_are_parsed_from_cards_without_affecting_order()
    {
        var (expected, result) = ParseFixture("summer-2025-tv");
        var capture = AssertValid(result);

        // Fixtures include rating, premiere timestamp, and synopsis on some cards.
        var details = expected.GetProperty("detailsExample");
        var first = capture.Occurrences[0];
        Assert.Contains(details.GetProperty("position0Rating").GetString()!, first.Details.Rating, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(first.Details.Premiere));

        var withSynopsis = capture.Occurrences.Single(o => o.Item.Value == details.GetProperty("synopsisId").GetInt32());
        Assert.Equal(details.GetProperty("synopsis").GetString(), withSynopsis.Details.Synopsis);

        // Rich live-like card HTML with studios, tags, metadata, premiere, and synopsis parses every detail field.
        var richHtml = """
            <!DOCTYPE html><html><head><link rel="canonical" href="/summer-2025/tv" /></head><body><main>
            <span data-controller="seasonal-chart">
            <button><span class="lcm3-button__label">Sort: Popularity</span></button>
            <article data-anime-id="4242" data-anime-item-id="4242" data-anime-item-romanized-native-title="Sample Romaji" data-anime-item-english-title="Sample English" data-anime-item-native-title="サンプル" class="lc-anime-details-card">
                <a data-anime-item-target="preferredTitle" href="/anime/4242">Sample English</a>
                <div class="lc-anime-poster-overlay-host__rating" title="8.88 out of 10 based on 1,234 user ratings">8.88</div>
                <ol class="lc-anime-details-card__studios"><li>Studio Alpha</li><li>Studio Beta</li></ol>
                <ol class="lc-anime-details-card__tags"><li>Action</li><li>Music</li></ol>
                <div class="lc-anime-details-card__premiere">Jul 5, 2025</div>
                <div class="lc-anime-details-card__metadata"><span>TV</span><span>12 eps</span><span>Manga</span></div>
                <div class="lc-anime-details-card__synopsis"><p>First paragraph.</p><p>Second paragraph.</p></div>
                <div class="lc-anime-details-card__footer">
                    <a href="https://anilist.co/anime/185660" class="anilist"></a>
                    <a href="https://myanimelist.net/anime/60543" class="mal"></a>
                </div>
            </article>
            </span></main></body></html>
            """;
        var rich = AssertValid(Parse(richHtml, Season.Summer, CatalogueCategory.Tv));
        var card = Assert.Single(rich.Occurrences);
        Assert.Equal("8.88 out of 10 based on 1,234 user ratings", card.Details.Rating);
        Assert.Equal("Jul 5, 2025", card.Details.Premiere);
        Assert.Equal("TV · 12 eps · Manga", card.Details.Metadata);
        Assert.Equal(["Studio Alpha", "Studio Beta"], card.Details.Studios);
        Assert.Equal(["Action", "Music"], card.Details.Tags);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", card.Details.Synopsis);
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), card.Details.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), card.Details.MyAnimeListUrl);

        // Verify oracle order remained untouched on the full fixture.
        var oracle = expected.GetProperty("oracle").GetProperty("orderedIds").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(oracle, capture.Occurrences.Select(o => o.Item.Value));
    }

    [Fact]
    public void Image_urls_outside_the_verified_host_are_not_candidates()
    {
        var html = Fixture("summer-2025-movies").Replace("https://u.livechart.me/", "https://evil.example/", StringComparison.Ordinal);

        var capture = AssertValid(Parse(html, Season.Summer, CatalogueCategory.Movies));

        Assert.All(capture.Occurrences, o => Assert.Empty(o.ImageCandidates));
    }

    private static SeasonCapture AssertValid(SeasonPageResult result) => Assert.IsType<SeasonPageResult.Valid>(result).Capture;

    private static SeasonPageResult.Invalid AssertInvalid(SeasonPageResult result) => Assert.IsType<SeasonPageResult.Invalid>(result);

    private static SeasonPageResult Parse(string html, Season season, CatalogueCategory category, int year = 2025)
    {
        var key = new SeasonKey(year, season);
        return LiveChartSeasonParser.Parse(html, key, category, LiveChartRoutes.SeasonPage(key, category), Retrieved);
    }

    private static (JsonElement Expected, SeasonPageResult Result) ParseFixture(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixturePath(name + ".expected.json")));
        var expected = json.RootElement.Clone();
        var season = Enum.Parse<Season>(expected.GetProperty("season").GetString()!, ignoreCase: true);
        var category = expected.GetProperty("category").GetString() switch
        {
            "tv" => CatalogueCategory.Tv,
            "movies" => CatalogueCategory.Movies,
            "ovas" => CatalogueCategory.Ovas,
            "all" => CatalogueCategory.All,
            var other => throw new InvalidOperationException(other),
        };
        Assert.Equal(expected.GetProperty("route").GetString(), LiveChartRoutes.SeasonPage(new SeasonKey(expected.GetProperty("year").GetInt32(), season), category).AbsoluteUri);
        return (expected, Parse(Fixture(name), season, category, expected.GetProperty("year").GetInt32()));
    }

    private static string Fixture(string name) => File.ReadAllText(FixturePath(name + ".html"));

    private static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "fixtures", "livechart", file);
}
