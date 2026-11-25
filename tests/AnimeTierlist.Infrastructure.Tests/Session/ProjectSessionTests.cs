using System.Collections.Immutable;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Tests.Session;

/// <summary>
/// Session cases on real temporary SQLite projects: serialized durable edits, project-wide undo/redo through the
/// same rules, download bindings that survive undo only for their occurrence, and failed-save suspension.
/// </summary>
public sealed class ProjectSessionTests : IAsyncDisposable
{
    private static readonly LiveChartId A = new(201);
    private static readonly LiveChartId B = new(202);
    private static readonly LiveChartId C = new(203);
    private static readonly LiveChartId D = new(204);
    private static readonly AcquisitionContext Summer = new(2025, Season.Summer, CatalogueCategory.Tv);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private readonly CollectionId _collection = CollectionId.New();
    private readonly SourceSnapshot _first = Snapshot(A, B, C);
    private readonly SourceSnapshot _second = Snapshot(B, A, C, D);
    private FaultInjectingStore? _store;
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

    private ProjectSession Session => _session ?? throw new InvalidOperationException("Open the session first.");

    private FaultInjectingStore Store => _store ?? throw new InvalidOperationException("Open the session first.");

    [Fact]
    public async Task Edits_commit_in_order_and_the_reopened_project_equals_the_current_revision()
    {
        await OpenNewAsync();
        CreateCollection();
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        Accepted(Session.Execute("Move C first", s => Edit(s, c => CollectionOperations.Move(c, C, 0))));
        Accepted(Session.Execute("Rename", s => Edit(s, c => CollectionOperations.Rename(c, "Renamed"))));

        await Session.FlushAsync();

        Assert.Equal(SaveState.Saved, Session.SaveState);
        Assert.Equal(Describe(Session.Current), Describe(Session.Durable));
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
        Assert.Equal(["C", "A", "B"], Names(Session.Current.Collection(_collection)));
    }

    [Fact]
    public async Task Undo_and_redo_are_durable_edits()
    {
        await OpenNewAsync();
        CreateCollection();
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        Accepted(Session.Execute("Rename", s => Edit(s, c => CollectionOperations.Rename(c, "Renamed"))));

        Assert.Equal("Rename", Session.UndoLabel);
        Accepted(Session.Undo());
        Accepted(Session.Undo());
        Assert.Equal("Include A", Session.RedoLabel);
        Assert.False(Session.Current.Collection(_collection).Entry(A).Included);
        await Session.FlushAsync();
        Assert.Equal(Describe(Session.Current), Describe(Session.Durable));

        Accepted(Session.Redo());
        Assert.True(Session.Current.Collection(_collection).Entry(A).Included);
        Assert.Equal("Summer 2025", Session.Current.Collection(_collection).Name);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());

        // Undo history is session-only.
        Assert.Null(Session.UndoLabel);
        Assert.Null(Session.RedoLabel);
    }

    [Fact]
    public async Task A_new_command_clears_redo()
    {
        await OpenNewAsync();
        CreateCollection();
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        Accepted(Session.Undo());

        Accepted(Session.Execute("Include B", s => Edit(s, c => CollectionOperations.SetIncluded(c, [B], true))));

        Assert.Null(Session.RedoLabel);
        Assert.IsType<SessionResult.Unchanged>(Session.Redo());
    }

    [Fact]
    public async Task Rejected_and_unchanged_commands_leave_state_and_history_alone()
    {
        await OpenNewAsync();
        CreateCollection();
        var before = Session.Current;

        var rejected = Assert.IsType<SessionResult.Rejected>(Session.Execute("Move unknown", s => Edit(s, c => CollectionOperations.Move(c, new LiveChartId(999), 0))));
        var unchanged = Session.Execute("Nothing", s => s);

        var sameContent = Session.Execute("Exclude all again", s => Edit(s, c => CollectionOperations.SetAllIncluded(c, false)));

        Assert.Contains("999", rejected.Reason, StringComparison.Ordinal);
        Assert.IsType<SessionResult.Unchanged>(unchanged);
        Assert.IsType<SessionResult.Unchanged>(sameContent);
        Assert.Same(before, Session.Current);
        Assert.Equal("Create collection", Session.UndoLabel);
    }

    [Fact]
    public async Task Download_completion_is_not_undoable_and_survives_undo_of_unrelated_commands()
    {
        await OpenNewAsync();
        CreateCollection();
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));

        var request = Session.BeginCoverRequest(_collection, A);
        Accepted(Session.CompleteCoverDownload(request, Provenance(request.Occurrence, 'a'), Asset('a')));
        Assert.Equal("Include A", Session.UndoLabel);

        Accepted(Session.Undo());
        var entry = Session.Current.Collection(_collection).Entry(A);
        Assert.False(entry.Included);
        Assert.Equal(Hash('a'), entry.Cover?.Asset);

        Accepted(Session.Undo());
        Assert.Empty(Session.Current.Collections);
        Accepted(Session.Redo());
        Assert.Equal(Hash('a'), Session.Current.Collection(_collection).Entry(A).Cover?.Asset);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Download_evidence_binds_only_to_its_own_occurrence_across_refresh_undo_and_redo()
    {
        await OpenNewAsync();
        CreateCollection();
        var stale = Session.BeginCoverRequest(_collection, A);
        Accepted(Session.Execute("Refresh", s => Edit(s.WithSnapshot(_second), c => CollectionOperations.ApplyRefresh(c, _second, adoptSourceOrder: false))));

        // Completed for the previous observation: retained, but cannot attach to the new one.
        Assert.IsType<SessionResult.Unchanged>(Session.CompleteCoverDownload(stale, Provenance(stale.Occurrence, 'a'), Asset('a')));
        Assert.Null(Session.Current.Collection(_collection).Entry(A).Cover);

        var current = Session.BeginCoverRequest(_collection, A);
        Accepted(Session.CompleteCoverDownload(current, Provenance(current.Occurrence, 'b'), Asset('b')));

        Accepted(Session.Undo());
        var undone = Session.Current.Collection(_collection).Entry(A);
        Assert.Equal(stale.Occurrence, undone.AcceptedOccurrence);
        Assert.Equal(Hash('a'), undone.Cover?.Asset);

        Accepted(Session.Redo());
        var redone = Session.Current.Collection(_collection).Entry(A);
        Assert.Equal(current.Occurrence, redone.AcceptedOccurrence);
        Assert.Equal(Hash('b'), redone.Cover?.Asset);
        Assert.Contains(_first.Id, Session.Current.Snapshots.Keys);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Mark_missing_invalidates_in_flight_downloads_and_undo_restores_the_original_reference()
    {
        await OpenNewAsync();
        CreateCollection();
        var first = Session.BeginCoverRequest(_collection, A);
        Accepted(Session.CompleteCoverDownload(first, Provenance(first.Occurrence, 'a'), Asset('a')));
        var original = Session.Current.Collection(_collection).Entry(A).Cover;

        var inFlight = Session.BeginCoverRequest(_collection, A);
        Accepted(Session.Execute("Mark cover missing", s => Edit(s, c => CollectionOperations.MarkCoverMissing(c, A))));

        Assert.IsType<SessionResult.Stale>(Session.CompleteCoverDownload(inFlight, Provenance(inFlight.Occurrence, 'b'), Asset('b')));
        Assert.Null(Session.Current.Collection(_collection).Entry(A).Cover);

        Accepted(Session.Undo());
        Assert.Equal(original, Session.Current.Collection(_collection).Entry(A).Cover);

        // Redo repeats the decision; the earlier download evidence cannot silently reverse it.
        Accepted(Session.Redo());
        Assert.Null(Session.Current.Collection(_collection).Entry(A).Cover);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Failed_save_suspends_every_mutation_until_retry_succeeds()
    {
        await OpenNewAsync();
        CreateCollection();
        await Session.FlushAsync();
        var request = Session.BeginCoverRequest(_collection, A);

        Store.FailNextCommits(1);
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        await Session.FlushAsync();

        Assert.Equal(SaveState.Failed, Session.SaveState);
        Assert.IsType<Application.Storage.ProjectStoreException>(Session.Failure);
        Assert.True(Session.Current.Collection(_collection).Entry(A).Included);
        Assert.False(Session.Durable.Collection(_collection).Entry(A).Included);
        Assert.IsType<SessionResult.Suspended>(Session.Execute("Include B", s => Edit(s, c => CollectionOperations.SetIncluded(c, [B], true))));
        Assert.IsType<SessionResult.Suspended>(Session.Undo());
        Assert.IsType<SessionResult.Suspended>(Session.CompleteCoverDownload(request, Provenance(request.Occurrence, 'a'), Asset('a')));

        Assert.True(await Session.RetryAsync());

        Assert.Equal(SaveState.Saved, Session.SaveState);
        Assert.True(Session.Durable.Collection(_collection).Entry(A).Included);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Queued_writes_after_a_failure_wait_and_retry_commits_them_in_order()
    {
        await OpenNewAsync();
        CreateCollection();
        await Session.FlushAsync();
        var committed = Store.CommittedCount;
        var release = new TaskCompletionSource();
        Store.Gate = release.Task;
        Store.FailNextCommits(1);

        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        Accepted(Session.Execute("Move A last", s => Edit(s, c => CollectionOperations.Move(c, A, 2))));
        Assert.Equal(SaveState.Saving, Session.SaveState);
        release.SetResult();
        await Session.FlushAsync();

        Assert.Equal(SaveState.Failed, Session.SaveState);
        Assert.Equal(committed, Store.CommittedCount);

        Store.Gate = null;
        Assert.True(await Session.RetryAsync());
        Assert.Equal(committed + 2, Store.CommittedCount);
        Assert.Equal(["B", "C", "A"], Names(Session.Current.Collection(_collection)));
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Discard_drops_queued_edits_reloads_the_durable_revision_and_clears_history()
    {
        await OpenNewAsync();
        CreateCollection();
        await Session.FlushAsync();
        var saved = Describe(Session.Current);
        var request = Session.BeginCoverRequest(_collection, A);
        var release = new TaskCompletionSource();
        Store.Gate = release.Task;
        Store.FailNextCommits(1);

        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));
        Accepted(Session.Execute("Include B", s => Edit(s, c => CollectionOperations.SetIncluded(c, [B], true))));
        release.SetResult();
        await Session.FlushAsync();
        Store.Gate = null;
        Assert.Equal(SaveState.Failed, Session.SaveState);

        await Session.DiscardAsync(Ct);

        Assert.Equal(SaveState.Saved, Session.SaveState);
        Assert.Equal(saved, Describe(Session.Current));
        Assert.Null(Session.UndoLabel);
        Assert.Null(Session.RedoLabel);
        Assert.IsType<SessionResult.Stale>(Session.CompleteCoverDownload(request, Provenance(request.Occurrence, 'a'), Asset('a')));

        Accepted(Session.Execute("Include C", s => Edit(s, c => CollectionOperations.SetIncluded(c, [C], true))));
        await Session.FlushAsync();
        var reopened = await ReopenedAsync();
        Assert.Equal(Describe(Session.Current), reopened);
        Assert.Equal([C], Session.Current.Collection(_collection).IncludedEntries.Select(e => e.Item));
    }

    [Fact]
    public async Task Discard_is_unavailable_without_a_failed_save()
    {
        await OpenNewAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Session.DiscardAsync(Ct));
    }

    [Fact]
    public async Task Collection_deletion_is_rejected_while_used_and_otherwise_undoable()
    {
        await OpenNewAsync();
        CreateCollection();
        var board = BoardId.New();
        Accepted(Session.Execute("Include all", s => Edit(s, c => CollectionOperations.SetAllIncluded(c, true))));
        Accepted(Session.Execute("Create board", s => s.WithBoard(NewBoard(s, board, "Favourites"))));

        var rejected = Assert.IsType<SessionResult.Rejected>(Session.Execute("Delete collection", s => s.WithoutCollection(_collection)));
        Assert.Contains("Favourites", rejected.Reason, StringComparison.Ordinal);

        Accepted(Session.Execute("Delete board", s => s.WithoutBoard(board)));
        Accepted(Session.Execute("Delete collection", s => s.WithoutCollection(_collection)));
        await Session.FlushAsync();
        Assert.Empty(Session.Durable.Collections);

        Accepted(Session.Undo());
        Accepted(Session.Undo());
        Assert.Equal("Favourites", Session.Current.Board(board).Name);
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Random_commands_with_undo_and_redo_always_reopen_as_the_current_revision()
    {
        await OpenNewAsync();
        CreateCollection();
        var random = new Random(20261118);
        var boards = new List<BoardId>();
        for (var step = 0; step < 300; step++)
        {
            var items = Session.Current.Collections.TryGetValue(_collection, out var collection) ? collection.Entries.Select(e => e.Item).ToArray() : [];
            switch (random.Next(9))
            {
                case 0:
                    Session.Undo();
                    break;
                case 1:
                    Session.Redo();
                    break;
                case 2 when items.Length > 0:
                    var item = items[random.Next(items.Length)];
                    Session.Execute("Toggle", s => Edit(s, c => CollectionOperations.SetIncluded(c, [item], !c.Entry(item).Included)));
                    break;
                case 3 when items.Length > 0:
                    var moved = items[random.Next(items.Length)];
                    var index = random.Next(items.Length);
                    Session.Execute("Move", s => Edit(s, c => CollectionOperations.Move(c, moved, index)));
                    break;
                case 4 when collection is not null:
                    var id = BoardId.New();
                    if (Session.Execute("Create board", s => s.WithBoard(NewBoard(s, id, "Board " + step))) is SessionResult.Accepted)
                    {
                        boards.Add(id);
                    }

                    break;
                case 5 when boards.Count > 0:
                    var target = boards[random.Next(boards.Count)];
                    Session.Execute("Rank", s =>
                    {
                        var b = s.Board(target);
                        var candidates = b.Entries.Select(e => e.Item).ToArray();
                        return candidates.Length == 0
                            ? s
                            : s.WithBoard(BoardOperations.MoveToTier(b, candidates[random.Next(candidates.Length)], b.Tiers[random.Next(b.Tiers.Length)].Id, 0));
                    });
                    break;
                case 6 when boards.Count > 0:
                    var deleted = boards[random.Next(boards.Count)];
                    Session.Execute("Delete board", s => s.WithoutBoard(deleted));
                    break;
                case 7 when items.Length > 0:
                    var downloaded = items[random.Next(items.Length)];
                    var request = Session.BeginCoverRequest(_collection, downloaded);
                    var hash = "abcdef"[random.Next(6)];
                    Session.CompleteCoverDownload(request, Provenance(request.Occurrence, hash), Asset(hash));
                    break;
                case 8 when collection is not null && !Session.Current.Snapshots.ContainsKey(_second.Id):
                    Session.Execute("Refresh", s => Edit(s.WithSnapshot(_second), c => CollectionOperations.ApplyRefresh(c, _second, adoptSourceOrder: random.Next(2) == 0)));
                    break;
            }

            if (step % 50 == 49)
            {
                await Session.FlushAsync();
                Assert.Equal(SaveState.Saved, Session.SaveState);
                Assert.Equal(Describe(Session.Current), Describe(Session.Durable));
            }
        }

        await Session.FlushAsync();
        Assert.Equal(Describe(Session.Current), await ReopenedAsync());
    }

    [Fact]
    public async Task Undo_and_redo_preserve_current_item_extras_even_with_an_earlier_timestamp()
    {
        await OpenNewAsync();
        CreateCollection();

        var originalExtras = new AnimeExtraDetails(
            A,
            new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero),
            new SourceDetails("8.0/10", null, null, "Old synopsis", [], []),
            [new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=oldop"), AnimeVideoKind.Opening)],
            videosFetched: true,
            profileFetched: true);
        Accepted(Session.RecordItemExtras(originalExtras));

        // Take a user edit so undo captures originalExtras in its history snapshot.
        Accepted(Session.Execute("Include A", s => Edit(s, c => CollectionOperations.SetIncluded(c, [A], true))));

        // Simulate a later refresh whose machine clock was adjusted backwards (earlier timestamp than originalExtras).
        var refreshedExtras = new AnimeExtraDetails(
            A,
            new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero),
            new SourceDetails("8.7/10", null, null, "Fresh synopsis", [], []),
            [new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=newop"), AnimeVideoKind.Opening)],
            videosFetched: true,
            profileFetched: true);
        Accepted(Session.RecordItemExtras(refreshedExtras));
        await Session.FlushAsync();

        // Undo must keep the current refreshed cache rather than restoring originalExtras by timestamp.
        Accepted(Session.Undo());
        Assert.Equal(refreshedExtras, Session.Current.ItemExtras[A]);
        await Session.FlushAsync();

        // Redo must also keep the current refreshed cache.
        Accepted(Session.Redo());
        Assert.Equal(refreshedExtras, Session.Current.ItemExtras[A]);
        await ReopenedAsync();
        Assert.Equal(refreshedExtras, Session.Current.ItemExtras[A]);
    }

    private async Task OpenNewAsync()
    {
        _store = new FaultInjectingStore(await SqliteProjectStore.CreateAsync(Path.Combine(_root, "project"), Ct));
        _session = await ProjectSession.OpenAsync(_store, Ct);
    }

    /// <summary>Flushes, closes the session, reopens the project from disk, and returns its description.</summary>
    private async Task<string> ReopenedAsync()
    {
        await Session.FlushAsync();
        Assert.Equal(SaveState.Saved, Session.SaveState);
        await Session.DisposeAsync();
        _store = new FaultInjectingStore(await SqliteProjectStore.OpenAsync(Path.Combine(_root, "project"), Ct));
        _session = await ProjectSession.OpenAsync(_store, Ct);
        return Describe(_session.Current);
    }

    /// <summary>One command that creates the collection with every entry excluded, so tests include exactly what they need.</summary>
    private void CreateCollection() =>
        Accepted(Session.Execute("Create collection", s => s.WithSnapshot(_first).WithCollection(
            CollectionOperations.SetAllIncluded(CollectionOperations.Create(_collection, "Summer 2025", _first), included: false))));

    private ProjectState Edit(ProjectState state, Func<CollectionState, CollectionState> edit) =>
        state.WithCollection(edit(state.Collection(_collection)));

    private BoardState NewBoard(ProjectState state, BoardId id, string name)
    {
        var collection = state.Collection(_collection);
        return BoardOperations.Create(id, name, collection, e => state.ResolveTitle(collection, e), _ => true, BoardOperations.DefaultTiers());
    }

    private static void Accepted(SessionResult result) => Assert.IsType<SessionResult.Accepted>(result);

    private static SourceSnapshot Snapshot(params LiveChartId[] ids) =>
        new(SnapshotId.New(), Summer,
        [
            new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test",
                ids.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Title {id}", null, $"Romaji {id}", null), [])).ToImmutableArray()),
        ]);

    private static AssetHash Hash(char c) => new(new string(c, 64));

    private static AssetRecord Asset(char c) => new(Hash(c), AssetFormat.Jpeg, 350, 500, 1000 + c);

    private static CoverProvenance Provenance(OccurrenceRef occurrence, char c) =>
        new(occurrence, new Uri($"https://u.livechart.me/anime/{c}/large.jpg"), new Uri($"https://u.livechart.me/anime/{c}/large.jpg"), "Accept: image/jpeg,image/png", Hash(c));

    private static string[] Names(CollectionState collection) =>
        collection.Entries.Select(e => e.Item == A ? "A" : e.Item == B ? "B" : e.Item == C ? "C" : "D").ToArray();

    private static string Describe(ProjectState state) => string.Join("\n",
        state.Snapshots.Keys.Select(k => "S " + k).Order(StringComparer.Ordinal)
            .Concat(state.Assets.Values.Select(a => $"A {a.Hash}|{a.Format}|{a.Width}|{a.Height}|{a.ByteLength}").Order(StringComparer.Ordinal))
            .Concat(state.Collections.Values.OrderBy(c => c.Id.Value).Select(c =>
                $"C {c.Id}|{c.Name}|{c.Context}|{c.AppliedSnapshot}|{c.Language}|" + string.Join(";", c.Entries)))
            .Concat(state.Boards.Values.OrderBy(b => b.Id.Value).Select(b =>
                $"B {b.Id}|{b.Collection}|{b.Name}|" + string.Join(";", b.Tiers) + "|" + string.Join(";", b.Entries) + "|" +
                string.Join(";", b.Tiers.Select(t => t.Id + "=" + string.Join(",", b.Placements[t.Id]))))));
}
