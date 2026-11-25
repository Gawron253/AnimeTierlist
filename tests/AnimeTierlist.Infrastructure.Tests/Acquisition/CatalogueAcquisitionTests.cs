using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Core;

namespace AnimeTierlist.Infrastructure.Tests.Acquisition;

/// <summary>Complete annual snapshots only, calendar order, failed versus verified-empty seasons, and staging resume.</summary>
public sealed class CatalogueAcquisitionTests
{
    private static readonly AcquisitionContext Year2025 = new(2025, null, CatalogueCategory.Tv);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Year_is_composed_winter_first_with_first_occurrence_identity_and_all_occurrences_kept()
    {
        var source = new FakeSource()
            .Valid(Season.Winter, 1, 2)
            .Valid(Season.Spring, 3, 1)
            .Valid(Season.Summer, 4)
            .Valid(Season.Fall, 2, 5);

        var complete = Assert.IsType<CatalogueOutcome.Complete>(await Acquire(source, new CatalogueStaging()));

        Assert.Equal([Season.Winter, Season.Spring, Season.Summer, Season.Fall], source.Requested);
        Assert.Equal([1, 2, 3, 4, 5], complete.Snapshot.Sequence.Select(s => s.Item.Value));
        Assert.Equal(5, complete.UniqueItems);
        Assert.Equal(7, complete.RawOccurrences);
        Assert.Equal(Season.Winter, complete.Snapshot.Sequence.Single(s => s.Item.Value == 1).FirstOccurrence.Season.Season);
    }

    [Fact]
    public async Task Failed_fall_is_not_an_empty_fall_and_resume_fetches_only_what_is_missing()
    {
        var staging = new CatalogueStaging();
        var source = new FakeSource()
            .Valid(Season.Winter, 1)
            .Valid(Season.Spring, 2)
            .Valid(Season.Summer, 3)
            .Fail(Season.Fall, SeasonPageFailure.Transport);

        var incomplete = Assert.IsType<CatalogueOutcome.Incomplete>(await Acquire(source, staging));

        Assert.Equal(new SeasonKey(2025, Season.Fall), incomplete.FailedSeason);
        Assert.Equal(SeasonPageFailure.Transport, incomplete.Failure);
        Assert.Equal(3, incomplete.StagedSeasons.Length);
        Assert.Equal(3, staging.Count);

        source.Requested.Clear();
        source.Valid(Season.Fall);
        var complete = Assert.IsType<CatalogueOutcome.Complete>(await Acquire(source, staging));

        Assert.Equal([Season.Fall], source.Requested);
        Assert.Equal([new SeasonKey(2025, Season.Fall)], complete.VerifiedEmptySeasons);
        Assert.Equal([1, 2, 3], complete.Snapshot.Sequence.Select(s => s.Item.Value));
        Assert.Equal(0, staging.Count);
    }

    [Fact]
    public async Task Changed_layout_stops_further_requests_and_commits_nothing()
    {
        var source = new FakeSource()
            .Valid(Season.Winter, 1)
            .Fail(Season.Spring, SeasonPageFailure.ChangedLayout)
            .Valid(Season.Summer, 3)
            .Valid(Season.Fall, 4);

        var incomplete = Assert.IsType<CatalogueOutcome.Incomplete>(await Acquire(source, new CatalogueStaging()));

        Assert.Equal(SeasonPageFailure.ChangedLayout, incomplete.Failure);
        Assert.Equal([Season.Winter, Season.Spring], source.Requested);
    }

    [Fact]
    public async Task Paused_requests_are_reported_as_resumable()
    {
        var source = new FakeSource().Fail(Season.Winter, SeasonPageFailure.Paused);

        var incomplete = Assert.IsType<CatalogueOutcome.Incomplete>(await Acquire(source, new CatalogueStaging()));

        Assert.True(incomplete.IsPaused);
    }

    [Fact]
    public async Task A_completed_acquisition_never_reuses_its_pages_for_the_next_one()
    {
        var staging = new CatalogueStaging();
        var source = new FakeSource().Valid(Season.Winter, 1).Valid(Season.Spring, 2).Valid(Season.Summer, 3).Valid(Season.Fall, 4);
        await Acquire(source, staging);
        source.Requested.Clear();

        await Acquire(source, staging);

        Assert.Equal(4, source.Requested.Count);
    }

    private static Task<CatalogueOutcome> Acquire(FakeSource source, CatalogueStaging staging) =>
        CatalogueAcquisition.AcquireAsync(source, staging, Year2025, RetryBudget.CreateDefault(), null, Ct);

    private sealed class FakeSource : ICatalogueSource
    {
        private readonly Dictionary<Season, SeasonPageResult> _pages = [];

        public List<Season> Requested { get; } = [];

        public FakeSource Valid(Season season, params int[] ids)
        {
            var key = new SeasonKey(2025, season);
            _pages[season] = new SeasonPageResult.Valid(new SeasonCapture(key, CatalogueCategory.Tv, new Uri($"https://www.livechart.me/{season.ToString().ToLowerInvariant()}-2025/tv"),
                DateTimeOffset.UnixEpoch, "test", ids.Select((id, i) => new SourceOccurrence(i, new LiveChartId(id), new SourceTitles($"T{id}", null, null, null), [])).ToImmutableArray()));
            return this;
        }

        public FakeSource Fail(Season season, SeasonPageFailure failure)
        {
            _pages[season] = new SeasonPageResult.Invalid(failure, failure.ToString());
            return this;
        }

        public Task<SeasonPageResult> FetchSeasonAsync(SeasonKey season, CatalogueCategory category, RetryBudget budget, CancellationToken cancellationToken)
        {
            Requested.Add(season.Season);
            return Task.FromResult(_pages[season.Season]);
        }
    }
}
