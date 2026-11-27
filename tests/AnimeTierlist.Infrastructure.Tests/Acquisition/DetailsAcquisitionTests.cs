using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Storage;
using AnimeTierlist.Infrastructure.Tests.Session;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Tests.Acquisition;

/// <summary>Bulk details runs through a real session: resume, partial results, stops, durability, and curation safety.</summary>
public sealed class DetailsAcquisitionTests : IAsyncDisposable
{
    private static readonly LiveChartId A = new(501);
    private static readonly LiveChartId B = new(502);
    private static readonly LiveChartId C = new(503);
    private static readonly DateTimeOffset Retrieved = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private ProjectSession? _session;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ProjectFolder => Path.Combine(_root, "project");

    [Fact]
    public async Task Run_fetches_every_show_in_order_saves_durably_and_leaves_curation_and_undo_alone()
    {
        var collection = await CreateAsync(A, B, C);
        var before = _session!.Current.Collection(collection);
        var undo = _session.UndoLabel;
        var source = new FakeDetails();
        var reports = new List<DetailsRunReport>();

        var report = await Run([A, B, C], source, new SyncProgress(reports.Add));

        Assert.Equal((3, 3, 0, DetailsRunOutcome.Completed), (report.Total, report.Fetched, report.Failed, report.Outcome));
        Assert.Equal([(A, true, false), (A, false, true), (B, true, false), (B, false, true), (C, true, false), (C, false, true)], source.Requests);
        Assert.Equal([0, 1, 2, 3], reports.Select(r => r.Done));
        Assert.Equal(before, _session.Current.Collection(collection));
        Assert.Equal(undo, _session.UndoLabel);

        // Reopening from disk shows every show's details were committed.
        await _session.DisposeAsync();
        _session = await ProjectSession.OpenAsync(await SqliteProjectStore.OpenAsync(ProjectFolder, Ct), Ct);
        Assert.All([A, B, C], item =>
        {
            var extras = _session.Current.ItemExtras[item];
            Assert.True(extras.VideosFetched && extras.ProfileFetched);
            Assert.Equal("TV · Manga", extras.Details.Metadata);
        });
    }

    [Fact]
    public async Task Cached_pages_are_never_requested_again()
    {
        await CreateAsync(A, B, C);
        Accepted(_session!.RecordItemExtras(new AnimeExtraDetails(A, Retrieved, new SourceDetails(metadata: "TV · Original"), [], videosFetched: true, profileFetched: true)));
        Accepted(_session.RecordItemExtras(new AnimeExtraDetails(B, Retrieved, SourceDetails.Empty, [Op(B)], videosFetched: true, profileFetched: false)));
        var source = new FakeDetails();

        var report = await Run([A, B, C], source);

        Assert.Equal(2, report.Total);
        Assert.Equal([(B, false, true), (C, true, false), (C, false, true)], source.Requests);
        var b = _session.Current.ItemExtras[B];
        Assert.Equal([Op(B)], b.Videos); // Videos fetched earlier stay next to the newly fetched profile.
        Assert.True(b.ProfileFetched);
        Assert.Equal("TV · Original", _session.Current.ItemExtras[A].Details.Metadata);
    }

    [Fact]
    public async Task A_failed_page_keeps_the_other_page_and_the_run_continues()
    {
        await CreateAsync(A, B);
        var source = new FakeDetails { ProfileFailures = { [A] = SeasonPageFailure.WrongPage } };

        var report = await Run([A, B], source);

        Assert.Equal((2, 1, 1, DetailsRunOutcome.Completed), (report.Total, report.Fetched, report.Failed, report.Outcome));
        var a = _session!.Current.ItemExtras[A];
        Assert.True(a.VideosFetched);
        Assert.False(a.ProfileFetched);
        Assert.True(AnimeDetailsAcquisition.NeedsDetails(_session.Current, A));
        Assert.False(AnimeDetailsAcquisition.NeedsDetails(_session.Current, B));

        // The next run asks only for the page that failed.
        source.ProfileFailures.Clear();
        source.Requests.Clear();
        await Run([A, B], source);
        Assert.Equal([(A, false, true)], source.Requests);
    }

    [Theory]
    [InlineData(SeasonPageFailure.Paused, DetailsRunOutcome.Paused)]
    [InlineData(SeasonPageFailure.Unreachable, DetailsRunOutcome.Unreachable)]
    public async Task Pause_or_unreachable_stops_the_run_without_trying_later_shows(SeasonPageFailure failure, DetailsRunOutcome outcome)
    {
        await CreateAsync(A, B, C);
        var source = new FakeDetails { VideoFailures = { [B] = failure } };

        var report = await Run([A, B, C], source);

        Assert.Equal(outcome, report.Outcome);
        Assert.Equal((1, 1), (report.Fetched, report.Failed));
        Assert.Equal([A, B], source.Requests.Select(r => r.Item).Distinct());
        Assert.False(_session!.Current.ItemExtras.ContainsKey(C));
    }

    [Fact]
    public async Task Cancelling_keeps_shows_and_pages_fetched_so_far()
    {
        await CreateAsync(A, B, C);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var source = new FakeDetails { AfterFetch = item => { if (item == B) { cts.Cancel(); } } };

        var report = await AnimeDetailsAcquisition.AcquireAsync(_session!, [A, B, C], source, RetryBudget.CreateDefault(), null, cts.Token);

        Assert.Equal(DetailsRunOutcome.Cancelled, report.Outcome);
        Assert.True(_session!.Current.ItemExtras.ContainsKey(A));
        Assert.DoesNotContain(C, source.Requests.Select(r => r.Item));

        // Stopped between B's videos and profile requests: the videos are kept and the next run asks only for the profile.
        Assert.Equal((true, false), (_session.Current.ItemExtras[B].VideosFetched, _session.Current.ItemExtras[B].ProfileFetched));
        source.Requests.Clear();
        await Run([B], new FakeDetails());
        Assert.False(AnimeDetailsAcquisition.NeedsDetails(_session.Current, B));
    }

    [Fact]
    public async Task A_failed_commit_reports_details_as_not_saved()
    {
        FaultInjectingStore? store = null;
        _session = await ProjectSession.OpenAsync(store = new FaultInjectingStore(await SqliteProjectStore.CreateAsync(ProjectFolder, Ct)), Ct);
        await CreateAsync(A);
        store.FailNextCommits(10);

        var report = await Run([A], new FakeDetails());

        Assert.Equal(DetailsRunOutcome.NotSaved, report.Outcome);
        Assert.Equal(SaveState.Failed, _session.SaveState);
    }

    private Task<DetailsRunReport> Run(LiveChartId[] items, FakeDetails source, IProgress<DetailsRunReport>? progress = null) =>
        AnimeDetailsAcquisition.AcquireAsync(_session!, items, source, RetryBudget.CreateDefault(), progress, Ct);

    private async Task<CollectionId> CreateAsync(params LiveChartId[] ids)
    {
        _session ??= await ProjectSession.OpenAsync(await SqliteProjectStore.CreateAsync(ProjectFolder, Ct), Ct);
        var context = new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv);
        var snapshot = new SourceSnapshot(SnapshotId.New(), context,
        [
            new SeasonCapture(context.RequiredSeasons[0], context.Category, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test",
                ids.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Title {id}", null, null, null), [])).ToImmutableArray()),
        ]);
        var collection = CollectionId.New();
        Accepted(_session.Execute("Create", s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.Create(collection, "Collection", snapshot))));
        return collection;
    }

    private static AnimeVideo Op(LiveChartId item) =>
        new("OP", new Uri($"https://www.youtube.com/watch?v=op{item.Value:D9}"), AnimeVideoKind.Opening);

    private static void Accepted(SessionResult result) => Assert.IsType<SessionResult.Accepted>(result);

    /// <summary>Reports synchronously so the test sees every report before the run returns.</summary>
    private sealed class SyncProgress(Action<DetailsRunReport> report) : IProgress<DetailsRunReport>
    {
        public void Report(DetailsRunReport value) => report(value);
    }

    private sealed class FakeDetails : IAnimeDetailsSource
    {
        public List<(LiveChartId Item, bool Videos, bool Profile)> Requests { get; } = [];

        public Dictionary<LiveChartId, SeasonPageFailure> VideoFailures { get; } = [];

        public Dictionary<LiveChartId, SeasonPageFailure> ProfileFailures { get; } = [];

        public Action<LiveChartId>? AfterFetch { get; init; }

        public Task<AnimeDetailsFetchOutcome> FetchAsync(LiveChartId item, bool fetchVideos, bool fetchProfile, RetryBudget budget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((item, fetchVideos, fetchProfile));
            ImmutableArray<AnimeVideo>? videos = null;
            AnimeDetailsFetchFailure? videosFailure = null;
            if (fetchVideos)
            {
                if (VideoFailures.TryGetValue(item, out var vf))
                {
                    videosFailure = new AnimeDetailsFetchFailure(vf, $"videos {vf}");
                }
                else
                {
                    videos = [Op(item)];
                }
            }

            SourceDetails? profile = null;
            AnimeDetailsFetchFailure? profileFailure = null;
            if (fetchProfile && videosFailure?.Kind is not (SeasonPageFailure.Paused or SeasonPageFailure.Unreachable))
            {
                if (ProfileFailures.TryGetValue(item, out var pf))
                {
                    profileFailure = new AnimeDetailsFetchFailure(pf, $"profile {pf}");
                }
                else
                {
                    profile = new SourceDetails(metadata: "TV · Manga");
                }
            }

            AfterFetch?.Invoke(item);
            return Task.FromResult(new AnimeDetailsFetchOutcome(Retrieved, videos, videosFailure, profile, profileFailure));
        }
    }
}
