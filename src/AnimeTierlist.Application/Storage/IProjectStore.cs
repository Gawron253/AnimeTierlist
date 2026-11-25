using System.Collections.Immutable;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Storage;

/// <summary>Everything persisted in a project's database, as validated domain objects.</summary>
public sealed record ProjectData(
    ImmutableDictionary<SnapshotId, SourceSnapshot> Snapshots,
    ImmutableArray<CollectionState> Collections,
    ImmutableArray<BoardState> Boards,
    ImmutableDictionary<AssetHash, AssetRecord> Assets,
    ImmutableDictionary<LiveChartId, AnimeExtraDetails>? ItemExtras = null)
{
    /// <summary>Cached on-demand anime details and video links keyed by LiveChart ID.</summary>
    public ImmutableDictionary<LiveChartId, AnimeExtraDetails> ItemExtras { get; init; } =
        ItemExtras ?? ImmutableDictionary<LiveChartId, AnimeExtraDetails>.Empty;
}

/// <summary>
/// One durable unit of change. Every part commits in a single transaction or not at all.
/// Collections and boards are written as complete states; snapshots and assets are append-only.
/// </summary>
public sealed record ProjectChange
{
    public ImmutableArray<SourceSnapshot> AddSnapshots { get; init; } = [];

    public ImmutableArray<AssetRecord> AddAssets { get; init; } = [];

    public ImmutableArray<CollectionState> PutCollections { get; init; } = [];

    public ImmutableArray<BoardState> PutBoards { get; init; } = [];

    public ImmutableArray<BoardId> DeleteBoards { get; init; } = [];

    public ImmutableArray<CollectionId> DeleteCollections { get; init; } = [];

    /// <summary>When set, renumbers all collections to this explicit order.</summary>
    public ImmutableArray<CollectionId>? CollectionOrder { get; init; }

    /// <summary>When set, renumbers all boards to this explicit order.</summary>
    public ImmutableArray<BoardId>? BoardOrder { get; init; }

    /// <summary>On-demand anime details and video links to upsert inside the project database.</summary>
    public ImmutableArray<AnimeExtraDetails> PutItemExtras { get; init; } = [];

    public bool IsEmpty =>
        AddSnapshots.IsEmpty && AddAssets.IsEmpty && PutCollections.IsEmpty && PutBoards.IsEmpty && DeleteBoards.IsEmpty && DeleteCollections.IsEmpty
        && CollectionOrder is null && BoardOrder is null && PutItemExtras.IsEmpty;
}

/// <summary>Durable project storage. Implementations serialize writes and treat an edit as saved only after commit.</summary>
public interface IProjectStore : IAsyncDisposable
{
    Task<ProjectData> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Commits the change atomically. Throws <see cref="ProjectStoreException"/> when the change is rejected or cannot be saved.</summary>
    Task CommitAsync(ProjectChange change, CancellationToken cancellationToken);
}

public sealed class ProjectStoreException : Exception
{
    public ProjectStoreException()
    {
    }

    public ProjectStoreException(string message)
        : base(message)
    {
    }

    public ProjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
