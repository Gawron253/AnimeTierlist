using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Catalogue;

/// <summary>Reads one validated season page from the catalogue source.</summary>
public interface ICatalogueSource
{
    Task<SeasonPageResult> FetchSeasonAsync(SeasonKey season, CatalogueCategory category, RetryBudget budget, CancellationToken cancellationToken);
}

/// <summary>
/// Session-only staging of validated season captures. A partial acquisition can resume from here during the
/// session, but staging is never a committed snapshot and never changes collection rows. Captures used to build a
/// complete snapshot are removed, so a later acquisition always fetches fresh pages.
/// </summary>
public sealed class CatalogueStaging
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(SeasonKey, CatalogueCategory), SeasonCapture> _captures = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _captures.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _captures.Clear();
        }
    }

    internal bool TryGet(SeasonKey season, CatalogueCategory category, out SeasonCapture capture)
    {
        lock (_gate)
        {
            return _captures.TryGetValue((season, category), out capture!);
        }
    }

    internal void Put(SeasonCapture capture)
    {
        lock (_gate)
        {
            _captures[(capture.Season, capture.Category)] = capture;
        }
    }

    internal void Remove(IEnumerable<SeasonCapture> captures)
    {
        lock (_gate)
        {
            foreach (var capture in captures)
            {
                _captures.Remove((capture.Season, capture.Category));
            }
        }
    }
}

/// <summary>Progress of a catalogue acquisition: the season being requested and how many are validated.</summary>
public sealed record CatalogueProgress(SeasonKey Season, int ValidatedSeasons, int RequiredSeasons);

/// <summary>The result of acquiring a whole season or year in one category.</summary>
public abstract record CatalogueOutcome
{
    private CatalogueOutcome()
    {
    }

    /// <summary>Every required season validated. Counts use unique items; raw source occurrences are reported separately.</summary>
    public sealed record Complete(SourceSnapshot Snapshot) : CatalogueOutcome
    {
        public int UniqueItems => Snapshot.Sequence.Length;

        public int RawOccurrences => Snapshot.OccurrenceCount;

        public ImmutableArray<SeasonKey> VerifiedEmptySeasons =>
            Snapshot.Captures.Where(c => c.Occurrences.IsEmpty).Select(c => c.Season).ToImmutableArray();
    }

    /// <summary>
    /// A required season failed or requests are paused. Nothing may be committed. Seasons validated so far stay in
    /// staging so a retry or resume requests only what is still missing.
    /// </summary>
    public sealed record Incomplete(SeasonKey FailedSeason, SeasonPageFailure Failure, string Detail, ImmutableArray<SeasonKey> StagedSeasons) : CatalogueOutcome
    {
        public bool IsPaused => Failure == SeasonPageFailure.Paused;
    }
}

public static class CatalogueAcquisition
{
    /// <summary>
    /// Acquires every season the context requires, in calendar order, one request at a time. Stops at the first
    /// failed season (a failed season is never an empty one). A complete result is a new, uncommitted snapshot.
    /// </summary>
    public static async Task<CatalogueOutcome> AcquireAsync(
        ICatalogueSource source,
        CatalogueStaging staging,
        AcquisitionContext context,
        RetryBudget budget,
        IProgress<CatalogueProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);

        var required = context.RequiredSeasons;
        var captures = new List<SeasonCapture>(required.Count);
        foreach (var season in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CatalogueProgress(season, captures.Count, required.Count));
            if (!staging.TryGet(season, context.Category, out var capture))
            {
                var result = await source.FetchSeasonAsync(season, context.Category, budget, cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case SeasonPageResult.Valid valid:
                        capture = valid.Capture;
                        if (capture.Season != season || capture.Category != context.Category)
                        {
                            return new CatalogueOutcome.Incomplete(season, SeasonPageFailure.WrongPage, "The source returned a different season or category.", Staged(captures));
                        }

                        staging.Put(capture);
                        break;
                    case SeasonPageResult.Invalid invalid:
                        return new CatalogueOutcome.Incomplete(season, invalid.Failure, invalid.Detail, Staged(captures));
                    default:
                        throw new InvalidOperationException("Unknown season page result.");
                }
            }

            captures.Add(capture);
        }

        progress?.Report(new CatalogueProgress(required[^1], captures.Count, required.Count));
        var snapshot = new SourceSnapshot(SnapshotId.New(), context, captures);
        staging.Remove(captures);
        return new CatalogueOutcome.Complete(snapshot);
    }

    private static ImmutableArray<SeasonKey> Staged(List<SeasonCapture> captures) => captures.Select(c => c.Season).ToImmutableArray();
}
