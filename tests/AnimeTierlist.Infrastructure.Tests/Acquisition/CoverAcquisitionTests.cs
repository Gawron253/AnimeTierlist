using System.Collections.Concurrent;
using System.Collections.Immutable;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Tests.Acquisition;

/// <summary>Cover acquisition through a real session: order independence, caching, reuse, repair, and counts.</summary>
public sealed class CoverAcquisitionTests : IAsyncDisposable
{
    private static readonly LiveChartId A = new(401);
    private static readonly LiveChartId B = new(402);
    private static readonly LiveChartId C = new(403);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAssets _assets = new();
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

    private ProjectSession Session => _session!;

    [Fact]
    public async Task Completion_order_C_A_B_leaves_source_order_and_selection_order_unchanged()
    {
        var collection = await CreateAsync(Season.Summer, A, B, C);
        var source = new FakeCovers(completionOrder: [C, A, B]);

        // Three slots so C can finish first while A and B wait for their turn.
        var report = await Run(collection, source, concurrency: 3);
        Accepted(Session.Execute("Exclude all", s => Edit(s, collection, c => CollectionOperations.SetAllIncluded(c, false))));
        Accepted(Session.Execute("Select C", s => Edit(s, collection, c => CollectionOperations.SetIncluded(c, [C], true))));
        Accepted(Session.Execute("Select A", s => Edit(s, collection, c => CollectionOperations.SetIncluded(c, [A], true))));

        Assert.Equal([C, A, B], source.CompletedItems);
        Assert.Equal((3, 3, 0), (report.Total, report.Completed, report.Failed));
        var state = Session.Current.Collection(collection);
        Assert.Equal([A, B, C], state.Entries.Select(e => e.Item));
        Assert.Equal([A, C], state.IncludedEntries.Select(e => e.Item));
        Assert.All(state.Entries, e => Assert.NotNull(e.Cover));
    }

    [Fact]
    public async Task Valid_selected_covers_are_not_downloaded_again()
    {
        var collection = await CreateAsync(Season.Summer, A, B);
        var source = new FakeCovers();
        await Run(collection, source);
        source.Requests.Clear();

        var report = await Run(collection, source);

        Assert.Empty(source.Requests);
        Assert.Equal(2, report.Cached);
    }

    [Fact]
    public async Task Overlapping_collection_reuses_the_exact_verified_candidate_without_merging_identity()
    {
        var summer = await CreateAsync(Season.Summer, A, B);
        var source = new FakeCovers();
        await Run(summer, source);
        source.Requests.Clear();

        // A second collection observes A in its own snapshot with the same verified candidate URL.
        var other = CollectionId.New();
        var snapshot = Snapshot(new AcquisitionContext(2025, Season.Summer, CatalogueCategory.All), A, C);
        Accepted(Session.Execute("Create", s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.Create(other, "All", snapshot))));
        var report = await Run(other, source);

        Assert.Equal([C], source.Requests);
        Assert.Equal((1, 1), (report.Cached, report.Completed));
        var reused = Session.Current.Collection(other).Entry(A);
        Assert.Equal(reused.AcceptedOccurrence, reused.Cover!.Occurrence);
        Assert.Equal(snapshot.Id, reused.Cover.Occurrence.Snapshot);
        Assert.Equal(Session.Current.Collection(summer).Entry(A).Cover!.Asset, reused.Cover.Asset);
    }

    [Fact]
    public async Task Reuse_requires_the_same_request_context()
    {
        var summer = await CreateAsync(Season.Summer, A);
        await Run(summer, new FakeCovers());
        var other = CollectionId.New();
        var snapshot = Snapshot(new AcquisitionContext(2025, Season.Summer, CatalogueCategory.All), A);
        Accepted(Session.Execute("Create", s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.Create(other, "All", snapshot))));
        var differentContext = new FakeCovers(requestContext: "Accept: image/jpeg");

        await Run(other, differentContext);

        Assert.Equal([A], differentContext.Requests);
    }

    [Fact]
    public async Task Damaged_selection_is_repaired_only_with_its_exact_hash()
    {
        var collection = await CreateAsync(Season.Summer, A, B);
        var source = new FakeCovers();
        await Run(collection, source);
        var hashA = Session.Current.Collection(collection).Entry(A).Cover!.Asset;
        var hashB = Session.Current.Collection(collection).Entry(B).Cover!.Asset;
        _assets.Damaged.Add(hashA);
        _assets.Damaged.Add(hashB);
        source.Requests.Clear();
        source.DifferentBytesFor.Add(B);

        var report = await Run(collection, source);

        Assert.Equal([CoverStatus.Repaired, CoverStatus.NeedsReview], report.Entries.Select(e => e.Status));
        Assert.Equal(hashB, Session.Current.Collection(collection).Entry(B).Cover!.Asset);
    }

    [Fact]
    public async Task Missing_failed_and_paused_entries_are_counted_separately_and_never_as_success()
    {
        var collection = await CreateAsync(Season.Summer, A, B, C);
        var source = new FakeCovers();
        source.Results[A] = new CoverFetchResult.Missing("no image");
        source.Results[B] = new CoverFetchResult.Failed("HTTP 503");
        source.Results[C] = new CoverFetchResult.Paused("budget");

        var report = await Run(collection, source, concurrency: 1);

        Assert.Equal((3, 0, 1, 1, 1), (report.Total, report.Completed, report.Missing, report.Failed, report.Paused));
        Assert.All(Session.Current.Collection(collection).Entries, e => Assert.Null(e.Cover));
    }

    [Fact]
    public async Task Different_cover_is_offered_for_review_and_replaces_only_after_the_user_accepts_it()
    {
        var collection = await CreateAsync(Season.Summer, A);
        var source = new FakeCovers();
        await Run(collection, source);
        var broken = Session.Current.Collection(collection).Entry(A).Cover!;
        _assets.Damaged.Add(broken.Asset);
        source.DifferentBytesFor.Add(A);

        var review = Assert.Single((await Run(collection, source)).Entries);

        Assert.Equal(CoverStatus.NeedsReview, review.Status);
        var offer = Assert.IsType<CoverReplacement>(review.Replacement);
        Assert.NotEqual(broken.Asset, offer.Asset.Hash);
        Assert.Equal(broken.Occurrence, offer.Provenance.Occurrence);
        Assert.Equal(broken, Session.Current.Collection(collection).Entry(A).Cover);
        Assert.False(Session.Current.Assets.ContainsKey(offer.Asset.Hash));

        Accepted(await CoverRecovery.ReplaceAsync(Session, _assets, collection, A, offer, Ct));

        Assert.Equal(offer.Provenance, Session.Current.Collection(collection).Entry(A).Cover);
        Assert.Equal(offer.Asset, Session.Current.Assets[offer.Asset.Hash]);
        Assert.Equal("Use new cover", Session.UndoLabel);

        // Undo restores the broken reference and its provenance, not null.
        Accepted(Session.Undo());
        Assert.Equal(broken, Session.Current.Collection(collection).Entry(A).Cover);
        Accepted(Session.Redo());
        await Session.FlushAsync();
        Assert.Equal(offer.Provenance, Session.Durable.Collection(collection).Entry(A).Cover);
    }

    [Fact]
    public async Task Mark_missing_clears_only_a_broken_cover_offline_and_blocks_a_late_download()
    {
        var collection = await CreateAsync(Season.Summer, A, B);
        var source = new FakeCovers();
        await Run(collection, source);
        var broken = Session.Current.Collection(collection).Entry(A).Cover!;
        var valid = Session.Current.Collection(collection).Entry(B).Cover!;
        _assets.Damaged.Add(broken.Asset);
        var late = Session.BeginCoverRequest(collection, A);

        Assert.Equal(CoverCondition.Broken, await CoverRecovery.InspectAsync(Session.Current, collection, A, _assets, Ct));
        Assert.Equal(CoverCondition.Valid, await CoverRecovery.InspectAsync(Session.Current, collection, B, _assets, Ct));
        Assert.IsType<SessionResult.Rejected>(await CoverRecovery.MarkMissingAsync(Session, _assets, collection, B, Ct));
        Accepted(await CoverRecovery.MarkMissingAsync(Session, _assets, collection, A, Ct));

        var entries = Session.Current.Collection(collection);
        Assert.Null(entries.Entry(A).Cover);
        Assert.Equal(valid, entries.Entry(B).Cover);
        Assert.Equal(CoverCondition.None, await CoverRecovery.InspectAsync(Session.Current, collection, A, _assets, Ct));

        // A download that started before the decision cannot reverse it.
        var replacement = new AssetRecord(new AssetHash(new string('e', 64)), AssetFormat.Jpeg, 350, 500, 1000);
        Assert.IsType<SessionResult.Stale>(Session.CompleteCoverDownload(late, broken with { Asset = replacement.Hash }, replacement));
        Assert.Null(Session.Current.Collection(collection).Entry(A).Cover);

        Accepted(Session.Undo());
        Assert.Equal(broken, Session.Current.Collection(collection).Entry(A).Cover);
    }

    [Fact]
    public async Task Replacement_is_refused_for_a_valid_cover_or_a_changed_selection()
    {
        var collection = await CreateAsync(Season.Summer, A);
        var source = new FakeCovers();
        await Run(collection, source);
        var current = Session.Current.Collection(collection).Entry(A).Cover!;
        _assets.Damaged.Add(current.Asset);
        source.DifferentBytesFor.Add(A);
        var offer = (await Run(collection, source)).Entries[0].Replacement!;

        _assets.Damaged.Remove(current.Asset);
        Assert.IsType<SessionResult.Rejected>(await CoverRecovery.ReplaceAsync(Session, _assets, collection, A, offer, Ct));

        _assets.Damaged.Add(current.Asset);
        Accepted(await CoverRecovery.MarkMissingAsync(Session, _assets, collection, A, Ct));
        Assert.IsType<SessionResult.Rejected>(await CoverRecovery.ReplaceAsync(Session, _assets, collection, A, offer, Ct));

        Accepted(Session.Undo());
        _assets.Damaged.Add(offer.Asset.Hash);
        Assert.IsType<SessionResult.Rejected>(await CoverRecovery.ReplaceAsync(Session, _assets, collection, A, offer, Ct));
        Assert.Equal(current, Session.Current.Collection(collection).Entry(A).Cover);
    }

    [Fact]
    public async Task A_refresh_between_scheduling_and_download_never_attaches_the_old_image_to_the_new_observation()
    {
        var collection = await CreateAsync(Season.Summer, A, B);
        var context = Session.Current.Collection(collection).Context;
        var refreshed = Snapshot(context, "v2", A, B);
        var source = new FakeCovers();
        source.OnFetch = item =>
        {
            if (item == A)
            {
                // B's download is already scheduled for its first observation when this refresh rebinds B.
                Accepted(Session.Execute("Apply update", s => s.WithSnapshot(refreshed).WithCollection(CollectionOperations.ApplyRefresh(s.Collection(collection), refreshed, false))));
            }
        };

        await Run(collection, source, concurrency: 1);

        var entry = Session.Current.Collection(collection).Entry(B);
        Assert.Equal(refreshed.Id, entry.AcceptedOccurrence.Snapshot);
        Assert.Null(entry.Cover);
        Assert.Equal(new Uri($"https://u.livechart.me/anime/{B}/large.jpg"), source.Fetched.Single(o => o.Item == B).ImageCandidates[0].Url);
    }

    [Fact]
    public async Task Repair_tries_the_recorded_variant_first()
    {
        var collection = await CreateAsync(Season.Summer, A);
        var occurrence = Session.Current.Observation(Session.Current.Collection(collection).Entry(A).AcceptedOccurrence);
        var small = new Uri($"https://u.livechart.me/anime/{A}/small.jpg");
        var source = new FakeCovers();
        var asset = new AssetRecord(new AssetHash(new string('9', 64)), AssetFormat.Jpeg, 175, 250, 500);
        source.Results[A] = new CoverFetchResult.Acquired(asset, small, small);
        await Run(collection, source);
        _assets.Damaged.Add(asset.Hash);
        source.Fetched.Clear();

        await Run(collection, source);

        Assert.Equal(small, Assert.Single(source.Fetched).ImageCandidates[0].Url);
        Assert.Equal(occurrence.ImageCandidates.Length, source.Fetched[0].ImageCandidates.Length);
    }

    [Fact]
    public async Task Unreachable_failures_are_a_counted_subset_of_failures()
    {
        var collection = await CreateAsync(Season.Summer, A, B, C);
        var source = new FakeCovers();
        source.Results[A] = new CoverFetchResult.Failed("could not connect", Unreachable: true);
        source.Results[B] = new CoverFetchResult.Failed("HTTP 503");

        var report = await Run(collection, source, concurrency: 1);

        Assert.Equal((3, 1, 2, 1), (report.Total, report.Completed, report.Failed, report.Unreachable));
        Assert.Equal([true, false, false], report.Entries.Select(e => e.Unreachable));
    }

    [Fact]
    public async Task Pause_stops_scheduling_remaining_downloads()
    {
        var collection = await CreateAsync(Season.Summer, A, B, C);
        var source = new FakeCovers();
        source.Results[A] = new CoverFetchResult.Paused("budget");

        var report = await Run(collection, source, concurrency: 1);

        Assert.Equal([A], source.Requests);
        Assert.Equal(3, report.Paused);
    }

    [Fact]
    public async Task Cancellation_reports_unfinished_entries_as_cancelled()
    {
        var collection = await CreateAsync(Season.Summer, A, B, C);
        using var cancel = new CancellationTokenSource();
        var source = new FakeCovers { OnFetch = item => { if (item == A) { cancel.Cancel(); } } };

        var report = await CoverAcquisition.AcquireAsync(Session, collection, source, _assets, RetryBudget.CreateDefault(), null, cancel.Token, maxConcurrency: 1);

        Assert.True(report.Cancelled >= 2);
        Assert.Equal(report.Total, report.Completed + report.Cancelled + report.Failed);
    }

    private async Task<CollectionId> CreateAsync(Season season, params LiveChartId[] ids)
    {
        _session ??= await ProjectSession.OpenAsync(await SqliteProjectStore.CreateAsync(Path.Combine(_root, "project"), Ct), Ct);
        var id = CollectionId.New();
        var snapshot = Snapshot(new AcquisitionContext(2025, season, CatalogueCategory.Tv), ids);
        Accepted(Session.Execute("Create", s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.Create(id, "Collection", snapshot))));
        return id;
    }

    private Task<CoverRunReport> Run(CollectionId collection, FakeCovers source, int concurrency = 2) =>
        CoverAcquisition.AcquireAsync(Session, collection, source, _assets, RetryBudget.CreateDefault(), null, Ct, concurrency);

    private static ProjectState Edit(ProjectState state, CollectionId id, Func<CollectionState, CollectionState> edit) =>
        state.WithCollection(edit(state.Collection(id)));

    private static void Accepted(SessionResult result) => Assert.IsType<SessionResult.Accepted>(result);

    private static SourceSnapshot Snapshot(AcquisitionContext context, params LiveChartId[] ids) => Snapshot(context, null, ids);

    private static SourceSnapshot Snapshot(AcquisitionContext context, string? version, params LiveChartId[] ids) =>
        new(SnapshotId.New(), context,
        [
            new SeasonCapture(context.RequiredSeasons[0], context.Category, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test",
                ids.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Title {id}", null, null, null),
                [
                    new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{id}/{version}large.jpg")),
                    new ImageCandidate("small", new Uri($"https://u.livechart.me/anime/{id}/{version}small.jpg")),
                ])).ToImmutableArray()),
        ]);

    /// <summary>Returns a deterministic asset per item; optionally completes in a chosen order.</summary>
    private sealed class FakeCovers(IReadOnlyList<LiveChartId>? completionOrder = null, string requestContext = "Accept: image/webp,image/png,image/jpeg") : ICoverSource
    {
        private readonly ConcurrentDictionary<LiveChartId, TaskCompletionSource> _turns = new();

        public List<LiveChartId> Requests { get; } = [];

        public List<SourceOccurrence> Fetched { get; } = [];

        public List<LiveChartId> CompletedItems { get; } = [];

        public Dictionary<LiveChartId, CoverFetchResult> Results { get; } = [];

        public HashSet<LiveChartId> DifferentBytesFor { get; } = [];

        public Action<LiveChartId>? OnFetch { get; set; }

        public string RequestContext => requestContext;

        public bool IsAllowedCandidate(Uri url) => url.Host == "u.livechart.me";

        public async Task<CoverFetchResult> FetchAsync(SourceOccurrence occurrence, RetryBudget budget, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(occurrence.Item);
                Fetched.Add(occurrence);
            }

            OnFetch?.Invoke(occurrence.Item);
            cancellationToken.ThrowIfCancellationRequested();
            if (completionOrder is not null)
            {
                await WaitForTurnAsync(occurrence.Item, cancellationToken);
            }

            lock (CompletedItems)
            {
                CompletedItems.Add(occurrence.Item);
            }

            if (completionOrder is not null)
            {
                ReleaseNext(occurrence.Item);
            }

            if (Results.TryGetValue(occurrence.Item, out var scripted))
            {
                return scripted;
            }

            var seed = DifferentBytesFor.Contains(occurrence.Item) ? 'f' : (char)('a' + (occurrence.Item.Value % 5));
            var asset = new AssetRecord(new AssetHash(new string(seed, 63) + (char)('0' + (occurrence.Item.Value % 10))), AssetFormat.Jpeg, 350, 500, 1000);
            return new CoverFetchResult.Acquired(asset, occurrence.ImageCandidates[0].Url, occurrence.ImageCandidates[0].Url);
        }

        private Task WaitForTurnAsync(LiveChartId item, CancellationToken cancellationToken)
        {
            if (completionOrder![0] == item && CompletedItems.Count == 0)
            {
                return Task.CompletedTask;
            }

            return _turns.GetOrAdd(item, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
        }

        private void ReleaseNext(LiveChartId finished)
        {
            var order = completionOrder!;
            var index = order.ToList().IndexOf(finished);
            if (index + 1 < order.Count)
            {
                _turns.GetOrAdd(order[index + 1], _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        }
    }

    private sealed class FakeAssets : IAssetStore
    {
        public HashSet<AssetHash> Damaged { get; } = [];

        public Task<AssetAvailability> CheckAsync(AssetRecord asset, CancellationToken cancellationToken) =>
            Task.FromResult(Damaged.Contains(asset.Hash) ? AssetAvailability.Damaged : AssetAvailability.Available);
    }
}
