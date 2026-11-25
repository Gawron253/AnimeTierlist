using System.Collections.Immutable;
using System.Text;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Http;

namespace AnimeTierlist.Infrastructure.LiveChart;

/// <summary>
/// On-demand LiveChart fetcher for per-anime videos (<c>/anime/{id}/videos</c>) and profile details and
/// AniList/MyAnimeList links (<c>/anime/{id}</c>), paced through <see cref="SourceHttpClient"/>.
/// Each requested page succeeds or fails independently so partial results are preserved.
/// </summary>
public sealed class LiveChartAnimeDetailsSource : IAnimeDetailsSource
{
    private const string Accept = "text/html,application/xhtml+xml";
    private const int MaxVideoPages = 10;

    private readonly SourceHttpClient _http;
    private readonly TimeProvider _time;

    public LiveChartAnimeDetailsSource(SourceHttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        _http = http;
        _time = time;
    }

    /// <summary>Fetches the requested videos (following validated pagination links) and/or profile pages for <paramref name="item"/> independently.</summary>
    public async Task<AnimeDetailsFetchOutcome> FetchAsync(
        LiveChartId item,
        bool fetchVideos,
        bool fetchProfile,
        RetryBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);

        ImmutableArray<AnimeVideo>? videos = null;
        AnimeDetailsFetchFailure? videosFailure = null;
        if (fetchVideos)
        {
            (videos, videosFailure) = await FetchAllVideosPagesAsync(item, budget, cancellationToken).ConfigureAwait(false);
        }

        SourceDetails? profileDetails = null;
        AnimeDetailsFetchFailure? profileFailure = null;
        if (fetchProfile)
        {
            var profileUrl = LiveChartRoutes.AnimePage(item);
            var profileFetch = await _http.GetAsync(profileUrl, RequestKind.Catalogue, Accept, _http.Policy.MaxCatalogueBytes, budget, cancellationToken).ConfigureAwait(false);
            if (profileFetch is HttpFetchResult.Success profileSuccess)
            {
                var (parsedDetails, profileError) = LiveChartAnimeDetailsParser.ParseAnimePage(Encoding.UTF8.GetString(profileSuccess.Body), item);
                if (profileError is not null)
                {
                    profileFailure = new AnimeDetailsFetchFailure(SeasonPageFailure.ChangedLayout, profileError);
                }
                else
                {
                    profileDetails = parsedDetails ?? SourceDetails.Empty;
                }
            }
            else if (profileFetch is HttpFetchResult.Failure failure)
            {
                profileFailure = MapFailure(failure, $"LiveChart anime page for anime #{item.Value}");
            }
        }

        return new AnimeDetailsFetchOutcome(_time.GetUtcNow(), videos, videosFailure, profileDetails, profileFailure);
    }

    private async Task<(ImmutableArray<AnimeVideo>? Videos, AnimeDetailsFetchFailure? Failure)> FetchAllVideosPagesAsync(
        LiveChartId item,
        RetryBudget budget,
        CancellationToken cancellationToken)
    {
        var firstUrl = LiveChartRoutes.AnimeVideosPage(item);
        var nextUrl = firstUrl;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var seenVideoUrls = new HashSet<string>(StringComparer.Ordinal);
        var collected = ImmutableArray.CreateBuilder<AnimeVideo>();
        var pageCount = 0;

        while (nextUrl is not null)
        {
            if (!visited.Add(LiveChartAnimeDetailsParser.NormalizeVideoDedupeKey(nextUrl)))
            {
                return (null, new AnimeDetailsFetchFailure(SeasonPageFailure.ChangedLayout, $"LiveChart videos pagination for anime #{item.Value} looped to an already-visited page."));
            }

            if (pageCount == 0)
            {
                visited.Add(LiveChartAnimeDetailsParser.NormalizeVideoDedupeKey(new Uri(firstUrl.AbsoluteUri + "?page=1")));
            }

            pageCount++;
            if (pageCount > MaxVideoPages)
            {
                return (null, new AnimeDetailsFetchFailure(SeasonPageFailure.ChangedLayout, $"LiveChart videos pagination for anime #{item.Value} exceeded the {MaxVideoPages}-page limit."));
            }

            var fetch = await _http.GetAsync(nextUrl, RequestKind.Catalogue, Accept, _http.Policy.MaxCatalogueBytes, budget, cancellationToken).ConfigureAwait(false);
            if (fetch is HttpFetchResult.Failure failure)
            {
                return (null, MapFailure(failure, $"LiveChart videos page {pageCount} for anime #{item.Value}"));
            }

            if (fetch is not HttpFetchResult.Success success)
            {
                return (null, new AnimeDetailsFetchFailure(SeasonPageFailure.Transport, $"LiveChart videos page {pageCount} for anime #{item.Value} returned an unexpected result."));
            }

            if (!MatchesRequestedVideosResponse(nextUrl, success.FinalUrl, firstUrl.AbsolutePath))
            {
                return (null, new AnimeDetailsFetchFailure(SeasonPageFailure.WrongPage, $"Expected LiveChart videos page '{nextUrl}' but received '{success.FinalUrl}'."));
            }

            var parsed = LiveChartAnimeDetailsParser.ParseVideosPageWithContinuation(Encoding.UTF8.GetString(success.Body), item, success.FinalUrl);
            if (parsed.Error is not null)
            {
                return (null, new AnimeDetailsFetchFailure(SeasonPageFailure.ChangedLayout, parsed.Error));
            }

            foreach (var video in parsed.Videos)
            {
                if (seenVideoUrls.Add(LiveChartAnimeDetailsParser.NormalizeVideoDedupeKey(video.Url)))
                {
                    collected.Add(video);
                }
            }

            nextUrl = parsed.NextPageUrl;
        }

        return (collected.ToImmutable(), null);
    }

    private static bool MatchesRequestedVideosResponse(Uri requestedUrl, Uri finalUrl, string expectedPath)
    {
        if (finalUrl.Scheme != Uri.UriSchemeHttps
            || !string.Equals(finalUrl.Host, LiveChartRoutes.Origin.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalUrl.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!LiveChartAnimeDetailsParser.TryReadPageNumber(requestedUrl, out var requestedPage)
            || !LiveChartAnimeDetailsParser.TryReadPageNumber(finalUrl, out var finalPage))
        {
            return false;
        }

        if (requestedPage is { } expected && expected > 1)
        {
            return finalPage == expected;
        }

        return finalPage is null || finalPage == 1;
    }

    private static AnimeDetailsFetchFailure MapFailure(HttpFetchResult.Failure failure, string label) => failure.Kind switch
    {
        HttpFetchFailure.NotFound => new(SeasonPageFailure.WrongPage, $"{label} was not found ({failure.Detail})."),
        HttpFetchFailure.TooLarge => new(SeasonPageFailure.TooLarge, failure.Detail),
        HttpFetchFailure.Paused => new(SeasonPageFailure.Paused, failure.Detail),
        HttpFetchFailure.Unreachable => new(SeasonPageFailure.Unreachable, failure.Detail),
        _ => new(SeasonPageFailure.Transport, failure.Detail),
    };
}
