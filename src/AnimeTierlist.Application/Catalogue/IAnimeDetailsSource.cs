using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Catalogue;

/// <summary>Failure details for one requested LiveChart page (videos or anime profile).</summary>
public sealed record AnimeDetailsFetchFailure(SeasonPageFailure Kind, string Detail);

/// <summary>
/// Outcome of an on-demand LiveChart fetch where the videos page (<c>/anime/{id}/videos</c>) and profile page
/// (<c>/anime/{id}</c>) succeed or fail independently so partial results are preserved and only failed pages retry.
/// </summary>
public sealed record AnimeDetailsFetchOutcome(
    DateTimeOffset RetrievedAtUtc,
    ImmutableArray<AnimeVideo>? Videos,
    AnimeDetailsFetchFailure? VideosFailure,
    SourceDetails? ProfileDetails,
    AnimeDetailsFetchFailure? ProfileFailure);

/// <summary>Fetches on-demand anime video links and extended profile details for a single LiveChart item.</summary>
public interface IAnimeDetailsSource
{
    /// <summary>
    /// Retrieves videos when <paramref name="fetchVideos"/> is true and full profile details/external links when
    /// <paramref name="fetchProfile"/> is true, reporting each page's result independently.
    /// </summary>
    Task<AnimeDetailsFetchOutcome> FetchAsync(
        LiveChartId item,
        bool fetchVideos,
        bool fetchProfile,
        RetryBudget budget,
        CancellationToken cancellationToken);
}
