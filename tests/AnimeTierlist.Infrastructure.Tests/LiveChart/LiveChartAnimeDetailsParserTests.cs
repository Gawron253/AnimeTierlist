using System.Net;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Http;
using AnimeTierlist.Infrastructure.LiveChart;
using AnimeTierlist.Infrastructure.Tests.Http;

namespace AnimeTierlist.Infrastructure.Tests.LiveChart;

/// <summary>Verifies on-demand parsing and paced HTTP fetching of LiveChart /anime/{id} and /anime/{id}/videos pages.</summary>
public sealed class LiveChartAnimeDetailsParserTests
{
    private static readonly LiveChartId DandadanId = new(12979);

    private const string SampleAnimePageHtml = """
        <!DOCTYPE html>
        <html><head><title>DAN DA DAN Season 2 | LiveChart.me</title></head><body>
        <div class="grid lc-anime-details-layout" data-controller="anime-details" data-anime-details-id="12979">
            <div data-anime-details-target="ratingBar" class="lc-rating-display">
                <span data-rating-display-target="ratingA"><span>8.5</span><span>/10</span></span>
                <div data-rating-display-target="contextA">3.8K Ratings</div>
            </div>
            <div><div>Status</div><a href="/anime?release_statuses=32">Finished</a></div>
            <div><div>Premiere</div><a href="/schedule?date=2025-07-03"><time datetime="2025-07-03T15:26:00Z">July 3, 2025</time></a></div>
            <div><div>Format</div><div><a href="/anime?anime_formats=4">TV</a></div></div>
            <div><div>Source</div><div><a href="/tags/22"><div>Manga</div></a></div></div>
            <div><div>Episodes</div><div data-viewing-progression-total-count-value="12">0/12</div></div>
            <div><div>Run time</div><div>24m</div></div>
            <div class="flex"><a href="/studios/230">Science SARU</a></div>
            <div class="flex"><a href="/tags/1">Action</a><a href="/tags/5">Comedy</a><a href="/tags/52">Supernatural</a></div>
            <div data-anime-details-target="synopsis">
                <div class="lc-markdown-html"><p>First paragraph of synopsis.</p><p>[Source: Crunchyroll]</p></div>
            </div>
            <div>
                <a href="https://anilist.co/anime/185660">AniList</a>
                <a href="https://myanimelist.net/anime/60543">MyAnimeList</a>
                <a href="https://evil.example/anime/1">Untrusted</a>
            </div>
        </div>
        </body></html>
        """;

    private const string SampleVideosPageHtml = """
        <!DOCTYPE html>
        <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
        <div class="lc-video" data-controller="video" data-video-id="40190" data-video-duration="PT1M46S">
            <a href="https://www.youtube.com/watch?v=BZWWBM9bi2g" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <img alt="Play video: Climax PV" />
            <div class="lc-media-spoiler__mask"><strong>SPOILERS</strong></div>
            <div data-video-target="durationBadge">1:46</div>
            <span data-video-target="auralTracklist">JA</span>
            <span data-video-target="textualTracklist">EN</span>
            <div class="lcm3-text--title-md">Climax PV</div>
        </div>
        <div class="lc-video" data-controller="video" data-video-id="39131" data-video-duration="PT1M42S">
            <a href="https://www.youtube.com/watch?v=AhNYcpN1pEU" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <div data-video-target="durationBadge">1:42</div>
            <span data-video-target="auralTracklist">JA</span>
            <span data-video-target="textualTracklist">None</span>
            <div class="lcm3-text--title-md">ED</div>
        </div>
        <div class="lc-video" data-controller="video" data-video-id="39124" data-video-duration="PT1M44S">
            <a href="https://www.youtube.com/watch?v=DCCRNzKvWRg" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <span data-video-target="auralTracklist">JA</span>
            <span data-video-target="textualTracklist">None</span>
            <div class="lcm3-text--title-md">OP</div>
        </div>
        <div class="lc-video" data-controller="video" data-video-id="39125" data-video-duration="PT1M31S">
            <a href="https://www.youtube.com/watch?v=op2video" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <div data-video-target="durationBadge">1:31</div>
            <span data-video-target="auralTracklist">JA</span>
            <div class="lcm3-text--title-md">OP 2</div>
        </div>
        <div class="lc-video" data-controller="video" data-video-id="38745" data-video-duration="PT30S">
            <a href="https://x.com/animeDANDADANen/status/1933177477272412307" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <div data-video-target="durationBadge">0:30</div>
            <span data-video-target="auralTracklist">JA</span>
            <span data-video-target="textualTracklist">EN</span>
            <div class="lcm3-text--title-md">Friendship &amp; Love CM</div>
        </div>
        <div class="lc-video" data-controller="video" data-video-id="99999">
            <a href="https://untrusted.example/bad" data-action="click->video#openPlayer" class="lc-interactive-surface__interaction-delegate"></a>
            <div class="lcm3-text--title-md">Untrusted Video</div>
        </div>
        </body></html>
        """;

    [Fact]
    public void Anime_page_parser_extracts_details_and_anilist_myanimelist_links()
    {
        var (details, error) = LiveChartAnimeDetailsParser.ParseAnimePage(SampleAnimePageHtml, DandadanId);

        Assert.Null(error);
        Assert.NotNull(details);
        Assert.Equal("8.5/10 (3.8K Ratings)", details.Rating);
        Assert.Equal("July 3, 2025", details.Premiere);
        Assert.Equal("TV · Manga · 12 eps × 24m · Finished", details.Metadata);
        Assert.Equal(["Science SARU"], details.Studios);
        Assert.Equal(["Action", "Comedy", "Supernatural"], details.Tags);
        Assert.Equal("First paragraph of synopsis.\n\n[Source: Crunchyroll]", details.Synopsis);
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), details.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), details.MyAnimeListUrl);
    }

    [Fact]
    public void Anime_page_parser_rejects_mismatched_anime_id()
    {
        var (details, error) = LiveChartAnimeDetailsParser.ParseAnimePage(SampleAnimePageHtml, new LiveChartId(99999));

        Assert.Null(details);
        Assert.NotNull(error);
    }

    [Fact]
    public void Anime_page_parser_rejects_canonical_only_empty_body_and_empty_details_container()
    {
        var canonicalOnly = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979" /></head><body></body></html>
            """;
        var (d1, e1) = LiveChartAnimeDetailsParser.ParseAnimePage(canonicalOnly, DandadanId);
        Assert.Null(d1);
        Assert.NotNull(e1);

        var emptyContainer = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979" /></head><body>
            <div data-anime-details-id="12979"></div>
            </body></html>
            """;
        var (d2, e2) = LiveChartAnimeDetailsParser.ParseAnimePage(emptyContainer, DandadanId);
        Assert.Null(d2);
        Assert.NotNull(e2);
    }

    [Fact]
    public void Videos_page_parser_extracts_op_ed_and_other_videos_and_skips_untrusted_hosts()
    {
        var (videos, error) = LiveChartAnimeDetailsParser.ParseVideosPage(SampleVideosPageHtml, DandadanId);

        Assert.Null(error);
        Assert.Equal(5, videos.Length);

        var op = Assert.Single(videos, v => v.Title == "OP");
        Assert.Equal(AnimeVideoKind.Opening, op.Kind);
        Assert.Equal("1:44", op.Duration); // Formatted from ISO PT1M44S when durationBadge is omitted.
        Assert.Equal("JA", op.AudioLanguage);
        Assert.Null(op.Subtitles);
        Assert.False(op.HasSpoilers);

        var op2 = Assert.Single(videos, v => v.Title == "OP 2");
        Assert.Equal(AnimeVideoKind.Opening, op2.Kind);

        var ed = Assert.Single(videos, v => v.Title == "ED");
        Assert.Equal(AnimeVideoKind.Ending, ed.Kind);

        var pv = Assert.Single(videos, v => v.Title == "Climax PV");
        Assert.Equal(AnimeVideoKind.Other, pv.Kind);
        Assert.True(pv.HasSpoilers);
        Assert.Equal("EN", pv.Subtitles);

        var cm = Assert.Single(videos, v => v.Title == "Friendship & Love CM");
        Assert.Equal(AnimeVideoKind.Other, cm.Kind);
        Assert.Equal("x.com", cm.Url.Host);
    }

    [Fact]
    public void Videos_page_parser_rejects_wrong_canonical_route()
    {
        var (videos, error) = LiveChartAnimeDetailsParser.ParseVideosPage(SampleVideosPageHtml, new LiveChartId(77777));

        Assert.Empty(videos);
        Assert.NotNull(error);
    }

    [Fact]
    public void Videos_page_parser_rejects_empty_body_and_unrecognized_cards_but_accepts_recognized_empty_state()
    {
        // Canonical URL with empty body must be rejected so a layout change cannot overwrite cached videos.
        var canonicalOnly = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body></body></html>
            """;
        var (v1, e1) = LiveChartAnimeDetailsParser.ParseVideosPage(canonicalOnly, DandadanId);
        Assert.Empty(v1);
        Assert.NotNull(e1);

        // Videos heading with unknown card markup and no empty-state message must also be rejected.
        var changedLayout = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos" /></head><body>
            <h1>Videos</h1>
            <div class="new-video-card">OP</div>
            </body></html>
            """;
        var (v2, e2) = LiveChartAnimeDetailsParser.ParseVideosPage(changedLayout, DandadanId);
        Assert.Empty(v2);
        Assert.NotNull(e2);

        // .lc-video cards missing recognized links/titles must be rejected.
        var brokenCards = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos" /></head><body>
            <div class="lc-video"><span>Changed markup</span></div>
            </body></html>
            """;
        var (v3, e3) = LiveChartAnimeDetailsParser.ParseVideosPage(brokenCards, DandadanId);
        Assert.Empty(v3);
        Assert.NotNull(e3);

        // Error messages containing "no videos" (such as "No videos could be loaded") must NOT count as an empty list.
        var errorMessage = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos" /></head><body>
            <h1>Videos</h1>
            <a href="/anime/12979/videos">All</a>
            <div class="lc-empty-state">No videos could be loaded. Please try again later.</div>
            </body></html>
            """;
        var (vErr, eErr) = LiveChartAnimeDetailsParser.ParseVideosPage(errorMessage, DandadanId);
        Assert.Empty(vErr);
        Assert.NotNull(eErr);

        // A .lc-video card with a title and an unrelated HTTP link (no LiveChart video delegate) must be rejected.
        var unrelatedLinkCard = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos" /></head><body>
            <div class="lc-video">
                <a href="https://unrelated.example/info">More info</a>
                <div class="lcm3-text--title-md">OP</div>
            </div>
            </body></html>
            """;
        var (vUnrelated, eUnrelated) = LiveChartAnimeDetailsParser.ParseVideosPage(unrelatedLinkCard, DandadanId);
        Assert.Empty(vUnrelated);
        Assert.NotNull(eUnrelated);

        // Recognized videos page structure + explicit empty state is accepted as an empty video list.
        var validEmpty = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos" /></head><body>
            <h1>Videos</h1>
            <a href="/anime/12979/videos">All</a>
            <div class="lc-empty-state">There are no videos for this anime yet.</div>
            </body></html>
            """;
        var (v4, e4) = LiveChartAnimeDetailsParser.ParseVideosPage(validEmpty, DandadanId);
        Assert.Null(e4);
        Assert.Empty(v4);
    }

    [Fact]
    public async Task LiveChartAnimeDetailsSource_follows_videos_pagination_and_rejects_incomplete_continuation()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ManualTime();
        var handler = new ScriptedHandler();
        var page1 = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video" data-controller="video" data-video-id="1">
                <a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a>
                <div class="lcm3-text--title-md">Main PV</div>
            </div>
            <nav><a rel="next" href="/anime/12979/videos?page=2">Next</a></nav>
            </body></html>
            """;
        var page2 = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=2" /></head><body>
            <div class="lc-video" data-controller="video" data-video-id="2">
                <a href="https://www.youtube.com/watch?v=oppage2" class="lc-interactive-surface__interaction-delegate"></a>
                <div class="lcm3-text--title-md">OP</div>
            </div>
            </body></html>
            """;

        // Run 1: page 1 succeeds, page 2 fails (404) -> videos must be reported as failed, NOT cached incomplete.
        handler.Add("https://www.livechart.me/anime/12979/videos", HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(page1), "text/html");
        handler.Add("https://www.livechart.me/anime/12979/videos?page=2", HttpStatusCode.NotFound);
        handler.Add("https://www.livechart.me/anime/12979/videos?page=2", HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(page2), "text/html");

        using var http = new SourceHttpClient(handler, SourceHttpPolicy.Default, "AnimeTierlist/test", time, time.DelayAsync, jitter: () => 0);
        var source = new LiveChartAnimeDetailsSource(http, time);

        var incomplete = await source.FetchAsync(DandadanId, fetchVideos: true, fetchProfile: false, RetryBudget.CreateDefault(), ct);
        Assert.Null(incomplete.Videos);
        Assert.NotNull(incomplete.VideosFailure);

        // Run 2: both pages succeed -> merged list contains both page 1 PV and page 2 OP.
        var complete = await source.FetchAsync(DandadanId, fetchVideos: true, fetchProfile: false, RetryBudget.CreateDefault(), ct);
        Assert.Null(complete.VideosFailure);
        Assert.NotNull(complete.Videos);
        Assert.Equal(["Main PV", "OP"], complete.Videos.Value.Select(v => v.Title));
    }

    [Fact]
    public void Videos_page_parser_resolves_relative_query_next_rejects_malformed_next_or_mixed_cards_and_preserves_case_sensitive_video_ids()
    {
        // S10: Relative query-only next link (`?page=2`) resolves against the current videos page.
        var relativeQueryNext = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=AbCdEf12345" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">OP</div></div>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=abcdef12345" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">ED</div></div>
            <a rel="next" href="?page=2">Next</a>
            </body></html>
            """;
        var parsedRelative = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(relativeQueryNext, DandadanId);
        Assert.Null(parsedRelative.Error);
        Assert.Equal(new Uri("https://www.livechart.me/anime/12979/videos?page=2"), parsedRelative.NextPageUrl);
        // S13: Distinct YouTube video IDs differing only by case must BOTH be preserved.
        Assert.Equal(["OP", "ED"], parsedRelative.Videos.Select(v => v.Title));

        // S10: Advertised next link with blank or missing href must fail rather than ending pagination silently.
        var blankNextHref = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            <a rel="next" href="">Next</a>
            </body></html>
            """;
        var parsedBlankNext = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(blankNextHref, DandadanId);
        Assert.NotNull(parsedBlankNext.Error);

        // S11: Page 2 request receiving a page 1 canonical URL must be rejected.
        var page1CanonicalOnPage2 = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            </body></html>
            """;
        var parsedWrongPage = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(
            page1CanonicalOnPage2,
            DandadanId,
            new Uri("https://www.livechart.me/anime/12979/videos?page=2"));
        Assert.NotNull(parsedWrongPage.Error);

        // S10: A disabled rel="next" marker must not hide a later active next-page marker.
        var disabledThenActiveNext = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            <a rel="next" class="disabled" aria-disabled="true">Next</a>
            <a rel="next" href="?page=2">Next</a>
            </body></html>
            """;
        var parsedAfterDisabled = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(disabledThenActiveNext, DandadanId);
        Assert.Null(parsedAfterDisabled.Error);
        Assert.Equal(new Uri("https://www.livechart.me/anime/12979/videos?page=2"), parsedAfterDisabled.NextPageUrl);

        // S11: Page 2 request receiving an invalid or conflicting canonical page (e.g., ?page=0 or ?page=1&page=2) must be rejected.
        var invalidZeroPageCanonical = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=0" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            </body></html>
            """;
        var parsedZeroPage = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(
            invalidZeroPageCanonical,
            DandadanId,
            new Uri("https://www.livechart.me/anime/12979/videos?page=2"));
        Assert.NotNull(parsedZeroPage.Error);

        // S11: Encoded NUL byte in page value (?page=2%00) must also be rejected despite int.TryParse accepting trailing NUL.
        var nulBytePageCanonical = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=2%00" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            </body></html>
            """;
        var parsedNulPage = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(
            nulBytePageCanonical,
            DandadanId,
            new Uri("https://www.livechart.me/anime/12979/videos?page=2"));
        Assert.NotNull(parsedNulPage.Error);

        // S12: Mixed page containing one valid PV card and one malformed OP card (missing delegate/title) must fail.
        var mixedValidAndMalformed = """
            <!DOCTYPE html>
            <html><head><link rel="canonical" href="https://www.livechart.me/anime/12979/videos?page=1" /></head><body>
            <div class="lc-video"><a href="https://www.youtube.com/watch?v=pv1" class="lc-interactive-surface__interaction-delegate"></a><div class="lcm3-text--title-md">Main PV</div></div>
            <div class="lc-video"><div class="lcm3-text--title-md">OP</div></div>
            </body></html>
            """;
        var (mixedVideos, mixedError) = LiveChartAnimeDetailsParser.ParseVideosPage(mixedValidAndMalformed, DandadanId);
        Assert.Empty(mixedVideos);
        Assert.NotNull(mixedError);
    }

    [Fact]
    public async Task LiveChartAnimeDetailsSource_fetches_pages_independently_and_preserves_partial_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ManualTime();
        var handler = new ScriptedHandler();
        handler.Add("https://www.livechart.me/anime/12979/videos", HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(SampleVideosPageHtml), "text/html");
        handler.Add("https://www.livechart.me/anime/12979", HttpStatusCode.NotFound);
        handler.Add("https://www.livechart.me/anime/12979", HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(SampleAnimePageHtml), "text/html");

        using var http = new SourceHttpClient(handler, SourceHttpPolicy.Default, "AnimeTierlist/test", time, time.DelayAsync, jitter: () => 0);
        var source = new LiveChartAnimeDetailsSource(http, time);

        // When profile fails (404), videos already fetched are preserved rather than discarded.
        var partial = await source.FetchAsync(DandadanId, fetchVideos: true, fetchProfile: true, RetryBudget.CreateDefault(), ct);
        Assert.NotNull(partial.Videos);
        Assert.Equal(5, partial.Videos.Value.Length);
        Assert.Null(partial.VideosFailure);
        Assert.Null(partial.ProfileDetails);
        Assert.NotNull(partial.ProfileFailure);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(time.Delays, d => d == TimeSpan.FromSeconds(5));

        // Retrying profile only sends 1 request (does not repeat /videos).
        var profileOnly = await source.FetchAsync(DandadanId, fetchVideos: false, fetchProfile: true, RetryBudget.CreateDefault(), ct);
        Assert.Null(profileOnly.Videos);
        Assert.Null(profileOnly.VideosFailure);
        Assert.Null(profileOnly.ProfileFailure);
        Assert.NotNull(profileOnly.ProfileDetails);
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), profileOnly.ProfileDetails.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), profileOnly.ProfileDetails.MyAnimeListUrl);
        Assert.Equal(3, handler.Requests.Count);
    }
}
