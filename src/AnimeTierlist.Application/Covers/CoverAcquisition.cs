using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Covers;

/// <summary>Downloads, validates, and installs a cover for one source occurrence through the approved provider adapter.</summary>
public interface ICoverSource
{
    /// <summary>The negotiated request context recorded in provenance (for example the Accept header).</summary>
    string RequestContext { get; }

    /// <summary>Whether a stored candidate URL is still an acceptable fetch target. Stored URLs never extend the allowlist.</summary>
    bool IsAllowedCandidate(Uri url);

    /// <summary>Tries the occurrence's verified candidates, largest first, and installs the first valid original.</summary>
    Task<CoverFetchResult> FetchAsync(SourceOccurrence occurrence, RetryBudget budget, CancellationToken cancellationToken);
}

public abstract record CoverFetchResult
{
    private CoverFetchResult()
    {
    }

    /// <summary>A validated original is installed in the asset store.</summary>
    public sealed record Acquired(AssetRecord Asset, Uri RequestedUrl, Uri FinalUrl) : CoverFetchResult;

    /// <summary>No candidate exists, or every candidate failed validation. The entry stays explicitly missing.</summary>
    public sealed record Missing(string Reason) : CoverFetchResult;

    /// <summary>
    /// Transient failures exhausted the bounded attempts; the entry remains resumable missing work.
    /// <paramref name="Unreachable"/> is true when no request reached the image host at all.
    /// </summary>
    public sealed record Failed(string Reason, bool Unreachable = false) : CoverFetchResult;

    /// <summary>A retry delay exceeded the run's budget; image requests are paused for manual resume.</summary>
    public sealed record Paused(string Reason) : CoverFetchResult;
}

public enum CoverStatus
{
    Pending,

    /// <summary>Downloaded and selected now (or retained as session evidence when the entry changed meanwhile).</summary>
    Downloaded,

    /// <summary>A valid cover was already selected, or a validated existing original was reused without a request.</summary>
    Cached,

    /// <summary>A damaged or missing original was restored with bytes of exactly the recorded hash.</summary>
    Repaired,

    Missing,
    Failed,

    /// <summary>The source now supplies a different cover for a broken selection; only reviewed recovery may change it.</summary>
    NeedsReview,

    Paused,
    Cancelled,
}

/// <summary>
/// A different validated cover offered for an invalid selection. Its bytes are installed in the asset store but not
/// recorded in the project until the user accepts it through <see cref="CoverRecovery.ReplaceAsync"/>.
/// </summary>
public sealed record CoverReplacement(CoverProvenance Provenance, AssetRecord Asset);

/// <summary>
/// One entry's outcome. <paramref name="Unreachable"/> marks a failure where the image host could not be reached.
/// <paramref name="Replacement"/> is set for <see cref="CoverStatus.NeedsReview"/>: the cover the source supplies now.
/// </summary>
public sealed record CoverEntryStatus(LiveChartId Item, CoverStatus Status, string? Detail, bool Unreachable = false, CoverReplacement? Replacement = null);

/// <summary>Unique-entry statuses in collection order, reconciled into counts. Raw source occurrences are reported separately.</summary>
public sealed record CoverRunReport(ImmutableArray<CoverEntryStatus> Entries, int RawOccurrences)
{
    public int Total => Entries.Length;

    public int Completed => Count(CoverStatus.Downloaded) + Count(CoverStatus.Repaired);

    public int Cached => Count(CoverStatus.Cached);

    public int Missing => Count(CoverStatus.Missing);

    public int Failed => Count(CoverStatus.Failed) + Count(CoverStatus.NeedsReview);

    /// <summary>Failed entries whose image host could not be reached at all (a subset of <see cref="Failed"/>).</summary>
    public int Unreachable => Entries.Count(e => e.Status == CoverStatus.Failed && e.Unreachable);

    public int Pending => Count(CoverStatus.Pending);

    public int Paused => Count(CoverStatus.Paused);

    public int Cancelled => Count(CoverStatus.Cancelled);

    private int Count(CoverStatus status) => Entries.Count(e => e.Status == status);
}

public static class CoverAcquisition
{
    /// <summary>
    /// Acquires covers for every entry of a collection. Entries with a valid selection are not redownloaded. An
    /// entry without one first reuses a validated original whose provenance matches one of its own verified
    /// candidates and the same request context, binding it to the entry's own occurrence; otherwise it downloads,
    /// at most <paramref name="maxConcurrency"/> at a time. Completion order never affects collection order.
    /// Broken selections are repaired only with bytes of their exact recorded hash.
    /// </summary>
    public static async Task<CoverRunReport> AcquireAsync(
        ProjectSession session,
        CollectionId collectionId,
        ICoverSource source,
        IAssetStore assets,
        RetryBudget budget,
        IProgress<CoverRunReport>? progress,
        CancellationToken cancellationToken,
        int maxConcurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var state = session.Current;
        var collection = state.Collection(collectionId);
        var raw = state.Snapshots[collection.AppliedSnapshot].OccurrenceCount;
        var statuses = collection.Entries.Select(e => new CoverEntryStatus(e.Item, CoverStatus.Pending, null)).ToArray();
        var gate = new Lock();
        CoverRunReport Snapshot()
        {
            lock (gate)
            {
                return new CoverRunReport([.. statuses], raw);
            }
        }

        void Set(int index, CoverStatus status, string? detail = null, bool unreachable = false, CoverReplacement? replacement = null)
        {
            lock (gate)
            {
                statuses[index] = statuses[index] with { Status = status, Detail = detail, Unreachable = unreachable, Replacement = replacement };
            }

            progress?.Report(Snapshot());
        }

        var availability = new Dictionary<AssetHash, AssetAvailability>();
        async Task<bool> IsAvailableAsync(AssetRecord asset)
        {
            if (!availability.TryGetValue(asset.Hash, out var known))
            {
                known = await assets.CheckAsync(asset, cancellationToken).ConfigureAwait(false);
                availability[asset.Hash] = known;
            }

            return known == AssetAvailability.Available;
        }

        var reusable = ReusableCovers(state, source.RequestContext);
        var jobs = new List<Func<CancellationToken, Task>>();
        var paused = 0;

        // Each entry's request is taken when it is scheduled, so the download, its recorded occurrence, and its
        // stale check all refer to the same accepted observation even if a refresh changes the entry meanwhile.
        (CoverRequest Request, SourceOccurrence Occurrence)? TryBegin(int position, LiveChartId item)
        {
            try
            {
                var request = session.BeginCoverRequest(collectionId, item);
                return (request, session.Current.Observation(request.Occurrence));
            }
            catch (KeyNotFoundException)
            {
                // The entry was removed, or a discard reloaded the project without its observation.
                Set(position, CoverStatus.Failed, "The entry is no longer in the collection.");
                return null;
            }
        }

        async Task RunEntryAsync(int position, Func<Task> work)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Set(position, CoverStatus.Failed, "The cover file could not be read or written: " + ex.Message);
            }
        }

        try
        {
            for (var index = 0; index < collection.Entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = collection.Entries[index];
                var position = index;
                if (entry.Cover is { } cover)
                {
                    var record = state.Assets[cover.Asset];
                    var available = false;
                    await RunEntryAsync(position, async () => available = await IsAvailableAsync(record).ConfigureAwait(false)).ConfigureAwait(false);
                    if (available)
                    {
                        Set(position, CoverStatus.Cached);
                        continue;
                    }

                    if (statuses[position].Status != CoverStatus.Pending)
                    {
                        continue;
                    }

                    var original = RecordedCandidateFirst(state.Observation(cover.Occurrence), cover, source.RequestContext);
                    jobs.Add(token => RunEntryAsync(position, async () =>
                    {
                        var result = await source.FetchAsync(original, budget, token).ConfigureAwait(false);
                        if (result is CoverFetchResult.Acquired different && different.Asset.Hash != cover.Asset)
                        {
                            // Never substitute a different hash in the background; offer it for reviewed recovery.
                            var offered = new CoverProvenance(cover.Occurrence, different.RequestedUrl, different.FinalUrl, source.RequestContext, different.Asset.Hash);
                            Set(position, CoverStatus.NeedsReview, "LiveChart now supplies a different cover. Review it, then use it or mark the cover missing.",
                                replacement: new CoverReplacement(offered, different.Asset));
                            return;
                        }

                        Report(position, result, onAcquired: _ => (CoverStatus.Repaired, null));
                    }));
                    continue;
                }

                if (TryBegin(position, entry.Item) is not { } begun)
                {
                    continue;
                }

                var (request, occurrence) = begun;

                (CoverProvenance Provenance, AssetRecord Asset)? reuse = null;
                await RunEntryAsync(position, async () => reuse = await FindReusableAsync(occurrence, reusable, source, IsAvailableAsync).ConfigureAwait(false)).ConfigureAwait(false);
                if (statuses[position].Status != CoverStatus.Pending)
                {
                    continue;
                }

                if (reuse is { } found)
                {
                    var provenance = new CoverProvenance(request.Occurrence, found.Provenance.RequestedUrl, found.Provenance.FinalUrl, source.RequestContext, found.Asset.Hash);
                    var (status, detail) = Bind(session.CompleteCoverDownload(request, provenance, found.Asset), CoverStatus.Cached);
                    Set(position, status, detail);
                    continue;
                }

                jobs.Add(token => RunEntryAsync(position, async () =>
                {
                    var result = await source.FetchAsync(occurrence, budget, token).ConfigureAwait(false);
                    Report(position, result, onAcquired: acquired =>
                    {
                        var provenance = new CoverProvenance(request.Occurrence, acquired.RequestedUrl, acquired.FinalUrl, source.RequestContext, acquired.Asset.Hash);
                        return Bind(session.CompleteCoverDownload(request, provenance, acquired.Asset), CoverStatus.Downloaded);
                    });
                }));
            }

            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken }, async (job, token) =>
            {
                // After a pause, schedule nothing new; the remaining work stays pending for resume.
                if (Volatile.Read(ref paused) == 0)
                {
                    await job(token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                for (var i = 0; i < statuses.Length; i++)
                {
                    if (statuses[i].Status == CoverStatus.Pending)
                    {
                        statuses[i] = statuses[i] with { Status = CoverStatus.Cancelled };
                    }
                }
            }
        }

        void Report(int position, CoverFetchResult result, Func<CoverFetchResult.Acquired, (CoverStatus, string?)> onAcquired)
        {
            switch (result)
            {
                case CoverFetchResult.Acquired acquired:
                    var (status, detail) = onAcquired(acquired);
                    Set(position, status, detail);
                    break;
                case CoverFetchResult.Missing missing:
                    Set(position, CoverStatus.Missing, missing.Reason);
                    break;
                case CoverFetchResult.Failed failed:
                    Set(position, CoverStatus.Failed, failed.Reason, failed.Unreachable);
                    break;
                case CoverFetchResult.Paused pause:
                    Interlocked.Exchange(ref paused, 1);
                    Set(position, CoverStatus.Paused, pause.Reason);
                    break;
            }
        }

        if (Volatile.Read(ref paused) == 1)
        {
            lock (gate)
            {
                for (var i = 0; i < statuses.Length; i++)
                {
                    if (statuses[i].Status == CoverStatus.Pending)
                    {
                        statuses[i] = statuses[i] with { Status = CoverStatus.Paused, Detail = "Image requests are paused; resume to continue." };
                    }
                }
            }
        }

        var report = Snapshot();
        progress?.Report(report);
        return report;
    }

    /// <summary>
    /// For repair, tries the selected cover's recorded URL first when its request context still applies, so the exact
    /// original bytes are preferred over a different variant that would only be offered for review.
    /// </summary>
    private static SourceOccurrence RecordedCandidateFirst(SourceOccurrence occurrence, CoverProvenance cover, string requestContext)
    {
        if (cover.RequestContext != requestContext || occurrence.ImageCandidates.FirstOrDefault(c => c.Url == cover.RequestedUrl) is not { } recorded)
        {
            return occurrence;
        }

        return occurrence with { ImageCandidates = [recorded, .. occurrence.ImageCandidates.Where(c => c != recorded)] };
    }

    private static (CoverStatus, string?) Bind(SessionResult result, CoverStatus success) => result switch
    {
        SessionResult.Accepted => (success, null),
        SessionResult.Unchanged => (success, "Kept for this source observation; the entry already has a cover or now uses a different observation."),
        SessionResult.Stale => (CoverStatus.Failed, "The entry's cover decision changed during the download."),
        SessionResult.Suspended => (CoverStatus.Paused, "Project changes are suspended after a failed save."),
        SessionResult.Rejected rejected => (CoverStatus.Failed, rejected.Reason),
        _ => (CoverStatus.Failed, "Unknown result."),
    };

    /// <summary>Selected covers in the project, keyed by exact requested URL, for the given request context only.</summary>
    private static Dictionary<Uri, (CoverProvenance Provenance, AssetRecord Asset)> ReusableCovers(ProjectState state, string requestContext)
    {
        var reusable = new Dictionary<Uri, (CoverProvenance, AssetRecord)>();
        foreach (var collection in state.Collections.Values)
        {
            foreach (var entry in collection.Entries)
            {
                if (entry.Cover is { } cover && cover.RequestContext == requestContext && state.Assets.TryGetValue(cover.Asset, out var asset))
                {
                    reusable.TryAdd(cover.RequestedUrl, (cover, asset));
                }
            }
        }

        return reusable;
    }

    private static async Task<(CoverProvenance Provenance, AssetRecord Asset)?> FindReusableAsync(
        SourceOccurrence occurrence,
        Dictionary<Uri, (CoverProvenance Provenance, AssetRecord Asset)> reusable,
        ICoverSource source,
        Func<AssetRecord, Task<bool>> isAvailable)
    {
        foreach (var candidate in occurrence.ImageCandidates)
        {
            if (source.IsAllowedCandidate(candidate.Url) && reusable.TryGetValue(candidate.Url, out var found) && await isAvailable(found.Asset).ConfigureAwait(false))
            {
                return found;
            }
        }

        return null;
    }
}
