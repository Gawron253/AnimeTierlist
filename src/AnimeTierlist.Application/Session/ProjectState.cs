using System.Collections.Immutable;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Session;

/// <summary>
/// One immutable revision of a whole project. Cross-aggregate rules (a collection's snapshot exists, a board's
/// collection exists, referenced assets are recorded, referenced collections cannot be deleted) are enforced here
/// so forward commands and undo/redo targets obey the same rules before reaching storage.
/// Snapshots and assets are append-only within a session.
/// </summary>
public sealed class ProjectState
{
    private ProjectState(
        ImmutableDictionary<SnapshotId, SourceSnapshot> snapshots,
        ImmutableDictionary<CollectionId, CollectionState> collections,
        ImmutableArray<CollectionId> collectionOrder,
        ImmutableDictionary<BoardId, BoardState> boards,
        ImmutableArray<BoardId> boardOrder,
        ImmutableDictionary<AssetHash, AssetRecord> assets,
        ImmutableDictionary<LiveChartId, AnimeExtraDetails> itemExtras)
    {
        Snapshots = snapshots;
        Collections = collections;
        CollectionOrder = collectionOrder;
        Boards = boards;
        BoardOrder = boardOrder;
        Assets = assets;
        ItemExtras = itemExtras;
    }

    public static ProjectState Empty { get; } = new(
        ImmutableDictionary<SnapshotId, SourceSnapshot>.Empty,
        ImmutableDictionary<CollectionId, CollectionState>.Empty,
        [],
        ImmutableDictionary<BoardId, BoardState>.Empty,
        [],
        ImmutableDictionary<AssetHash, AssetRecord>.Empty,
        ImmutableDictionary<LiveChartId, AnimeExtraDetails>.Empty);

    public ImmutableDictionary<SnapshotId, SourceSnapshot> Snapshots { get; }

    public ImmutableDictionary<CollectionId, CollectionState> Collections { get; }

    /// <summary>Explicit user-defined display order of collections in the project.</summary>
    public ImmutableArray<CollectionId> CollectionOrder { get; }

    public ImmutableDictionary<BoardId, BoardState> Boards { get; }

    /// <summary>Explicit user-defined display order of boards in the project.</summary>
    public ImmutableArray<BoardId> BoardOrder { get; }

    public ImmutableDictionary<AssetHash, AssetRecord> Assets { get; }

    /// <summary>Cached on-demand anime details and video links stored inside the project database.</summary>
    public ImmutableDictionary<LiveChartId, AnimeExtraDetails> ItemExtras { get; }

    /// <summary>Builds and validates a state from loaded data, preserving persisted collection and board order.</summary>
    public static ProjectState FromData(ProjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var state = new ProjectState(data.Snapshots, Empty.Collections, [], Empty.Boards, [], data.Assets, Empty.ItemExtras);
        state = data.Collections.Aggregate(state, (s, c) => s.WithCollection(c));
        state = data.Boards.Aggregate(state, (s, b) => s.WithBoard(b));
        return data.ItemExtras.Values.Aggregate(state, (s, e) => s.WithItemExtras(e));
    }

    public ProjectState WithSnapshot(SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Snapshots.TryGetValue(snapshot.Id, out var existing))
        {
            return ReferenceEquals(existing, snapshot) ? this : throw new InvalidOperationException($"Snapshot {snapshot.Id} already exists; snapshots are immutable.");
        }

        return new ProjectState(Snapshots.Add(snapshot.Id, snapshot), Collections, CollectionOrder, Boards, BoardOrder, Assets, ItemExtras);
    }

    public ProjectState WithAsset(AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (Assets.TryGetValue(asset.Hash, out var existing))
        {
            return existing == asset ? this : throw new InvalidOperationException($"Asset {asset.Hash} already exists with different recorded properties.");
        }

        return new ProjectState(Snapshots, Collections, CollectionOrder, Boards, BoardOrder, Assets.Add(asset.Hash, asset), ItemExtras);
    }

    /// <summary>Records on-demand fetched LiveChart details and videos for an item already present in the project.</summary>
    public ProjectState WithItemExtras(AnimeExtraDetails extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        if (!ContainsItem(extras.Item))
        {
            throw new InvalidOperationException($"Item {extras.Item} is not present in the project.");
        }

        if (ItemExtras.TryGetValue(extras.Item, out var existing) && existing == extras)
        {
            return this;
        }

        return new ProjectState(Snapshots, Collections, CollectionOrder, Boards, BoardOrder, Assets, ItemExtras.SetItem(extras.Item, extras));
    }

    public ProjectState WithCollection(CollectionState collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (Collections.TryGetValue(collection.Id, out var existing) && existing.Context != collection.Context)
        {
            throw new InvalidOperationException("A collection's acquisition context cannot change.");
        }

        var applied = RequireSnapshot(collection.AppliedSnapshot);
        if (applied.Context != collection.Context)
        {
            throw new InvalidOperationException("A collection's applied snapshot must come from its own acquisition context.");
        }

        foreach (var entry in collection.Entries)
        {
            RequireObservation(entry.AcceptedOccurrence, entry.Item, collection.Context);
            if (entry.SourceMissing == applied.Contains(entry.Item))
            {
                throw new InvalidOperationException($"Item {entry.Item} must be marked source-missing exactly when the applied snapshot lacks it.");
            }

            if (!entry.SourceMissing && (!applied.TryGetFirstOccurrence(entry.Item, out var first) || first != entry.AcceptedOccurrence))
            {
                throw new InvalidOperationException($"Observed item {entry.Item} must use its first occurrence in the applied snapshot.");
            }

            if (entry.Cover is { } cover)
            {
                RequireObservation(cover.Occurrence, entry.Item, collection.Context);
                RequireAsset(cover.Asset);
            }
        }

        var nextOrder = Collections.ContainsKey(collection.Id) ? CollectionOrder : CollectionOrder.Add(collection.Id);
        return new ProjectState(Snapshots, Collections.SetItem(collection.Id, collection), nextOrder, Boards, BoardOrder, Assets, ItemExtras);
    }

    /// <summary>Deletes a collection. Throws <see cref="CollectionInUseException"/> while any board references it.</summary>
    public ProjectState WithoutCollection(CollectionId id)
    {
        var collection = Collection(id);
        var users = Boards.Values.Where(b => b.Collection == id).OrderBy(b => b.Name, StringComparer.CurrentCulture).ToImmutableArray();
        if (!users.IsEmpty)
        {
            throw new CollectionInUseException(collection.Name, users.Select(b => b.Name).ToImmutableArray());
        }

        return new ProjectState(Snapshots, Collections.Remove(id), CollectionOrder.Remove(id), Boards, BoardOrder, Assets, ItemExtras);
    }

    /// <summary>Moves a collection to a new zero-based position in <see cref="CollectionOrder"/>.</summary>
    public ProjectState MoveCollection(CollectionId id, int newIndex)
    {
        var from = CollectionOrder.IndexOf(id);
        if (from < 0)
        {
            throw new KeyNotFoundException($"Collection {id} does not exist.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, CollectionOrder.Length);
        if (from == newIndex)
        {
            return this;
        }

        return new ProjectState(Snapshots, Collections, CollectionOrder.RemoveAt(from).Insert(newIndex, id), Boards, BoardOrder, Assets, ItemExtras);
    }

    public ProjectState WithBoard(BoardState board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (Boards.TryGetValue(board.Id, out var existing) && existing.Collection != board.Collection)
        {
            throw new InvalidOperationException("A board's source collection cannot change.");
        }

        Collection(board.Collection);
        foreach (var entry in board.Entries)
        {
            if (entry.PinnedCover is { } pinned)
            {
                RequireAsset(pinned);
            }
        }

        var nextOrder = Boards.ContainsKey(board.Id) ? BoardOrder : BoardOrder.Add(board.Id);
        return new ProjectState(Snapshots, Collections, CollectionOrder, Boards.SetItem(board.Id, board), nextOrder, Assets, ItemExtras);
    }

    public ProjectState WithoutBoard(BoardId id)
    {
        Board(id);
        return new ProjectState(Snapshots, Collections, CollectionOrder, Boards.Remove(id), BoardOrder.Remove(id), Assets, ItemExtras);
    }

    /// <summary>Moves a board to a new zero-based position in <see cref="BoardOrder"/>.</summary>
    public ProjectState MoveBoard(BoardId id, int newIndex)
    {
        var from = BoardOrder.IndexOf(id);
        if (from < 0)
        {
            throw new KeyNotFoundException($"Board {id} does not exist.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, BoardOrder.Length);
        if (from == newIndex)
        {
            return this;
        }

        return new ProjectState(Snapshots, Collections, CollectionOrder, Boards, BoardOrder.RemoveAt(from).Insert(newIndex, id), Assets, ItemExtras);
    }

    public CollectionState Collection(CollectionId id) =>
        Collections.TryGetValue(id, out var collection) ? collection : throw new KeyNotFoundException($"Collection {id} does not exist.");

    public BoardState Board(BoardId id) =>
        Boards.TryGetValue(id, out var board) ? board : throw new KeyNotFoundException($"Board {id} does not exist.");

    public SourceOccurrence Observation(OccurrenceRef occurrence) => RequireSnapshot(occurrence.Snapshot).Occurrence(occurrence);

    /// <summary>User override, then the collection's selected language, then the captured source-default title.</summary>
    public ResolvedTitle ResolveTitle(CollectionState collection, CollectionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(entry);
        return TitleResolver.Resolve(Observation(entry.AcceptedOccurrence).Titles, collection.Language, entry.TitleOverride);
    }

    /// <summary>
    /// The storage change that turns this revision into <paramref name="target"/>. Unchanged aggregates keep their
    /// instances, so only replaced collections, boards, and changed sidebar orders are written.
    /// </summary>
    internal ProjectChange ChangesTo(ProjectState target)
    {
        if (Snapshots.Keys.Any(id => !target.Snapshots.ContainsKey(id)) || Assets.Keys.Any(hash => !target.Assets.ContainsKey(hash)))
        {
            throw new InvalidOperationException("Snapshots and assets are append-only within a session.");
        }

        return new ProjectChange
        {
            AddSnapshots = target.Snapshots.Values.Where(s => !Snapshots.ContainsKey(s.Id)).ToImmutableArray(),
            AddAssets = target.Assets.Values.Where(a => !Assets.ContainsKey(a.Hash)).ToImmutableArray(),
            DeleteBoards = Boards.Keys.Where(id => !target.Boards.ContainsKey(id)).ToImmutableArray(),
            DeleteCollections = Collections.Keys.Where(id => !target.Collections.ContainsKey(id)).ToImmutableArray(),
            PutCollections = target.Collections.Values.Where(c => !Collections.TryGetValue(c.Id, out var old) || !ReferenceEquals(old, c)).ToImmutableArray(),
            PutBoards = target.Boards.Values.Where(b => !Boards.TryGetValue(b.Id, out var old) || !ReferenceEquals(old, b)).ToImmutableArray(),
            CollectionOrder = CollectionOrder.SequenceEqual(target.CollectionOrder) ? null : target.CollectionOrder,
            BoardOrder = BoardOrder.SequenceEqual(target.BoardOrder) ? null : target.BoardOrder,
            PutItemExtras = target.ItemExtras.Values.Where(e => !ItemExtras.TryGetValue(e.Item, out var old) || old != e).ToImmutableArray(),
        };
    }

    /// <summary>The same collections and boards with every snapshot, asset, and cached item extra of <paramref name="other"/> added without overwriting existing records.</summary>
    internal ProjectState WithAppendOnlyRecordsOf(ProjectState other)
    {
        var snapshots = Snapshots.SetItems(other.Snapshots.Where(p => !Snapshots.ContainsKey(p.Key)));
        var assets = Assets.SetItems(other.Assets.Where(p => !Assets.ContainsKey(p.Key)));
        var extras = ItemExtras.SetItems(other.ItemExtras.Where(p => !ItemExtras.ContainsKey(p.Key)));
        return new ProjectState(snapshots, Collections, CollectionOrder, Boards, BoardOrder, assets, extras);
    }

    private bool ContainsItem(LiveChartId item) =>
        Snapshots.Values.Any(s => s.Contains(item))
        || Collections.Values.Any(c => c.IndexOf(item) >= 0)
        || Boards.Values.Any(b => b.Entries.Any(e => e.Item == item));

    private SourceSnapshot RequireSnapshot(SnapshotId id) =>
        Snapshots.TryGetValue(id, out var snapshot) ? snapshot : throw new KeyNotFoundException($"Snapshot {id} does not exist.");

    private void RequireObservation(OccurrenceRef occurrence, LiveChartId item, AcquisitionContext context)
    {
        var snapshot = RequireSnapshot(occurrence.Snapshot);
        if (snapshot.Context != context || snapshot.Occurrence(occurrence).Item != item)
        {
            throw new InvalidOperationException($"Occurrence for item {item} must cite that item in a snapshot from the collection's acquisition context.");
        }
    }

    private void RequireAsset(AssetHash hash)
    {
        if (!Assets.ContainsKey(hash))
        {
            throw new InvalidOperationException($"Asset {hash} is not recorded in the project.");
        }
    }
}

/// <summary>A collection cannot be deleted while boards use it; the message names those boards.</summary>
public sealed class CollectionInUseException : InvalidOperationException
{
    public CollectionInUseException()
    {
        BoardNames = [];
    }

    public CollectionInUseException(string message)
        : base(message)
    {
        BoardNames = [];
    }

    public CollectionInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
        BoardNames = [];
    }

    public CollectionInUseException(string collectionName, ImmutableArray<string> boardNames)
        : base($"Collection '{collectionName}' is used by {boardNames.Length} board(s): {string.Join(", ", boardNames)}. Delete those boards first.")
    {
        BoardNames = boardNames;
    }

    public ImmutableArray<string> BoardNames { get; }
}
