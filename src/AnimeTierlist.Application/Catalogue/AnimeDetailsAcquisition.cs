using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Catalogue;

/// <summary>Why a bulk details run stopped, or <see cref="Running"/> for a progress report.</summary>
public enum DetailsRunOutcome
{
    Running,
    Completed,
    Cancelled,

    /// <summary>LiveChart asked the app to wait longer than the run's retry budget allows. Fetch again to resume.</summary>
    Paused,

    /// <summary>LiveChart could not be reached, so the remaining shows were not tried.</summary>
    Unreachable,

    /// <summary>Project saves failed or are suspended, so fetched details are not stored.</summary>
    NotSaved,
}

/// <summary>Progress of a bulk details run. <see cref="Total"/> counts only shows that still needed details.</summary>
public sealed record DetailsRunReport(int Total, int Fetched, int Failed, DetailsRunOutcome Outcome, string? LastFailure)
{
    public int Done => Fetched + Failed;
}

/// <summary>
/// Fetches LiveChart's videos and profile pages for every listed show that does not have them cached yet, one page
/// at a time through the paced details source, and stores each page in the project as soon as it arrives. Cached
/// pages are never requested again, so a stopped run resumes where it left off. Never changes curation or order.
/// </summary>
public static class AnimeDetailsAcquisition
{
    /// <summary>True when the project lacks the cached videos or profile details of <paramref name="item"/>.</summary>
    public static bool NeedsDetails(ProjectState state, LiveChartId item)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.ItemExtras.TryGetValue(item, out var extras) || !extras.VideosFetched || !extras.ProfileFetched;
    }

    public static async Task<DetailsRunReport> AcquireAsync(
        ProjectSession session,
        IReadOnlyList<LiveChartId> items,
        IAnimeDetailsSource source,
        RetryBudget budget,
        IProgress<DetailsRunReport>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);

        var pending = items.Distinct().Where(i => NeedsDetails(session.Current, i)).ToList();
        var fetched = 0;
        var failed = 0;
        string? lastFailure = null;
        var recorded = new Dictionary<LiveChartId, (bool Videos, bool Profile)>();
        DetailsRunReport Report(DetailsRunOutcome outcome) => new(pending.Count, fetched, failed, outcome, lastFailure);

        progress?.Report(Report(DetailsRunOutcome.Running));
        foreach (var item in pending)
        {
            AnimeDetailsFetchFailure? itemFailure = null;

            // Videos and profile are separate requests, each stored as soon as it arrives, so stopping between them
            // keeps the first page.
            foreach (var videosPage in new[] { true, false })
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return await FinishAsync(session, recorded, Report(DetailsRunOutcome.Cancelled)).ConfigureAwait(false);
                }

                session.Current.ItemExtras.TryGetValue(item, out var cached);
                if (videosPage ? cached is { VideosFetched: true } : cached is { ProfileFetched: true })
                {
                    continue;
                }

                AnimeDetailsFetchOutcome outcome;
                try
                {
                    outcome = await source.FetchAsync(item, fetchVideos: videosPage, fetchProfile: !videosPage, budget, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await FinishAsync(session, recorded, Report(DetailsRunOutcome.Cancelled)).ConfigureAwait(false);
                }

                if (outcome.Videos is not null || outcome.ProfileDetails is not null)
                {
                    // Merge with what is cached now, so a page fetched meanwhile from the details panel is kept.
                    session.Current.ItemExtras.TryGetValue(item, out var latest);
                    var extras = new AnimeExtraDetails(
                        item,
                        outcome.RetrievedAtUtc,
                        outcome.ProfileDetails ?? (latest is { ProfileFetched: true } ? latest.Details : SourceDetails.Empty),
                        outcome.Videos ?? (latest is { VideosFetched: true } ? latest.Videos : []),
                        outcome.Videos is not null || latest is { VideosFetched: true },
                        outcome.ProfileDetails is not null || latest is { ProfileFetched: true });
                    switch (session.RecordItemExtras(extras))
                    {
                        case SessionResult.Suspended:
                            lastFailure = "Saving is suspended.";
                            return await FinishAsync(session, recorded, Report(DetailsRunOutcome.NotSaved)).ConfigureAwait(false);
                        case SessionResult.Rejected rejected:
                            itemFailure ??= new AnimeDetailsFetchFailure(SeasonPageFailure.Transport, rejected.Reason);
                            break;
                        default:
                            // Accumulate the pages this run stored, so a later page never hides an earlier one.
                            var pages = recorded.GetValueOrDefault(item);
                            recorded[item] = (pages.Videos || videosPage, pages.Profile || !videosPage);
                            break;
                    }
                }

                if ((videosPage ? outcome.VideosFailure : outcome.ProfileFailure) is { } failure)
                {
                    itemFailure ??= failure;
                    if (failure.Kind is SeasonPageFailure.Paused or SeasonPageFailure.Unreachable)
                    {
                        failed++;
                        lastFailure = failure.Detail;
                        return await FinishAsync(session, recorded, Report(failure.Kind == SeasonPageFailure.Paused ? DetailsRunOutcome.Paused : DetailsRunOutcome.Unreachable)).ConfigureAwait(false);
                    }
                }
            }

            if (itemFailure is null)
            {
                fetched++;
            }
            else
            {
                failed++;
                lastFailure = itemFailure.Detail;
            }

            progress?.Report(Report(DetailsRunOutcome.Running));
        }

        return await FinishAsync(session, recorded, Report(cancellationToken.IsCancellationRequested ? DetailsRunOutcome.Cancelled : DetailsRunOutcome.Completed)).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for queued commits. A failed commit, or a recorded page missing from the saved state (for example after
    /// a failed commit was discarded), turns any outcome into <see cref="DetailsRunOutcome.NotSaved"/>.
    /// </summary>
    private static async Task<DetailsRunReport> FinishAsync(ProjectSession session, Dictionary<LiveChartId, (bool Videos, bool Profile)> recorded, DetailsRunReport report)
    {
        await session.FlushAsync().ConfigureAwait(false);
        var durable = session.Durable.ItemExtras;
        var lost = recorded.Any(r => !durable.TryGetValue(r.Key, out var saved)
            || (r.Value.Videos && !saved.VideosFetched)
            || (r.Value.Profile && !saved.ProfileFetched));
        return session.SaveState == SaveState.Failed || lost
            ? report with { Outcome = DetailsRunOutcome.NotSaved, LastFailure = session.Failure?.Message ?? report.LastFailure }
            : report;
    }
}
