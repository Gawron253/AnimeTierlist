using System.Collections.Immutable;

namespace AnimeTierlist.Core;

public readonly record struct BoardId(Guid Value)
{
    public static BoardId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct TierId(Guid Value)
{
    public static TierId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>Foreground ink chosen for legibility on a tier header background.</summary>
public enum TierInk
{
    Black,
    White,
}

/// <summary>An sRGB tier color. Color supplements a tier's name and position; it never identifies a tier alone.</summary>
public readonly record struct TierColor(byte Red, byte Green, byte Blue)
{
    /// <summary>Formats the color as uppercase six-digit sRGB hex.</summary>
    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    /// <summary>Computes the WCAG 2.x relative luminance in [0, 1].</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte value)
        {
            var srgb = value / 255.0;
            return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(Red)) + (0.7152 * Channel(Green)) + (0.0722 * Channel(Blue));
    }

    /// <summary>Computes the WCAG 2.x contrast ratio against another sRGB color.</summary>
    public double ContrastRatio(TierColor other)
    {
        var l1 = RelativeLuminance();
        var l2 = other.RelativeLuminance();
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Selects black or white ink, whichever achieves the higher WCAG contrast ratio.</summary>
    public TierInk PreferredInk()
    {
        var luminance = RelativeLuminance();
        var againstBlack = (luminance + 0.05) / 0.05;
        var againstWhite = 1.05 / (luminance + 0.05);
        return againstBlack >= againstWhite ? TierInk.Black : TierInk.White;
    }
}

public sealed record Tier
{
    public Tier(TierId id, string name, TierColor color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name.Trim();
        Color = color;
    }

    public TierId Id { get; }

    public string Name { get; init; }

    public TierColor Color { get; init; }
}

/// <summary>
/// Identifies one entry on a board: its LiveChart item plus its 1-based copy number.
/// Copy 1 is the original entry; copies 2, 3, ... are duplicated entries created on the board.
/// </summary>
public readonly record struct BoardEntryKey
{
    public BoardEntryKey(LiveChartId item, int copyNumber = 1)
    {
        if (copyNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(copyNumber), copyNumber, "Copy numbers are positive integers starting at 1.");
        }

        Item = item;
        CopyNumber = copyNumber;
    }

    public LiveChartId Item { get; }

    public int CopyNumber { get; }

    public bool IsDuplicate => CopyNumber > 1;

    public override string ToString() => CopyNumber == 1 ? Item.ToString() : $"{Item}#{CopyNumber}";
}

/// <summary>
/// A board's membership snapshot of one item copy. Title and cover are pinned at creation or explicit presentation
/// update, so later collection changes never rewrite a saved board. <see cref="PinnedCover"/> null means missing.
/// </summary>
public sealed record BoardEntry
{
    public BoardEntry(LiveChartId item, string pinnedTitle, AssetHash? pinnedCover, int copyNumber = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedTitle);
        if (copyNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(copyNumber), copyNumber, "Copy numbers are positive integers starting at 1.");
        }

        Item = item;
        PinnedTitle = pinnedTitle;
        PinnedCover = pinnedCover;
        CopyNumber = copyNumber;
    }

    public LiveChartId Item { get; }

    public string PinnedTitle { get; init; }

    public AssetHash? PinnedCover { get; init; }

    public int CopyNumber { get; }

    public BoardEntryKey Key => new(Item, CopyNumber);

    public bool IsDuplicate => CopyNumber > 1;
}

/// <summary>
/// A ranking board. <see cref="Entries"/> is the seed order. Each entry copy is in exactly one tier or in the
/// unranked pool; the pool always follows seed order and cannot be reordered manually.
/// </summary>
public sealed record BoardState
{
    public BoardState(BoardId id, CollectionId collection, string name, ImmutableArray<Tier> tiers, ImmutableArray<BoardEntry> entries, ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>> placements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(placements);
        if (tiers.IsDefault || entries.IsDefault)
        {
            throw new ArgumentException("Tiers and entries must be initialized.");
        }

        var tierIds = tiers.Select(t => t.Id).ToHashSet();
        if (tierIds.Count != tiers.Length)
        {
            throw new ArgumentException("Tier IDs must be unique.", nameof(tiers));
        }

        var members = new HashSet<BoardEntryKey>();
        foreach (var entry in entries)
        {
            if (!members.Add(entry.Key))
            {
                throw new ArgumentException($"Entry {entry.Key} appears more than once on the board.", nameof(entries));
            }
        }

        var placed = new HashSet<BoardEntryKey>();
        foreach (var (tierId, items) in placements)
        {
            if (!tierIds.Contains(tierId))
            {
                throw new ArgumentException($"Placement refers to unknown tier {tierId}.", nameof(placements));
            }

            foreach (var item in items)
            {
                if (!members.Contains(item))
                {
                    throw new ArgumentException($"Placed entry {item} is not a board entry.", nameof(placements));
                }

                if (!placed.Add(item))
                {
                    throw new ArgumentException($"Entry {item} has more than one tier placement.", nameof(placements));
                }
            }
        }

        Id = id;
        Collection = collection;
        Name = name.Trim();
        Tiers = tiers;
        Entries = entries;
        Placements = tiers.ToImmutableDictionary(t => t.Id, t => placements.TryGetValue(t.Id, out var items) ? items : []);
    }

    public BoardId Id { get; }

    public CollectionId Collection { get; }

    public string Name { get; }

    public ImmutableArray<Tier> Tiers { get; }

    /// <summary>Board entries in seed order.</summary>
    public ImmutableArray<BoardEntry> Entries { get; }

    /// <summary>Ordered entry keys for every tier (empty for tiers without entries).</summary>
    public ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>> Placements { get; }

    /// <summary>Unranked entry keys, always in seed order.</summary>
    public ImmutableArray<BoardEntryKey> Unranked
    {
        get
        {
            var placed = Placements.Values.SelectMany(v => v).ToHashSet();
            return Entries.Where(e => !placed.Contains(e.Key)).Select(e => e.Key).ToImmutableArray();
        }
    }

    public TierId? TierOf(BoardEntryKey entry)
    {
        foreach (var (tier, items) in Placements)
        {
            if (items.Contains(entry))
            {
                return tier;
            }
        }

        return null;
    }

    public TierId? TierOf(LiveChartId item) => TierOf(new BoardEntryKey(item));

    public bool Contains(BoardEntryKey entry) => Entries.Any(e => e.Key == entry);

    public bool Contains(LiveChartId item) => Entries.Any(e => e.Item == item);

    /// <summary>Returns all entries on this board for <paramref name="item"/>, in seed order.</summary>
    public ImmutableArray<BoardEntry> CopiesOf(LiveChartId item) => Entries.Where(e => e.Item == item).ToImmutableArray();

    internal BoardState With(string? name = null, ImmutableArray<Tier>? tiers = null, ImmutableArray<BoardEntry>? entries = null, ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>? placements = null) =>
        new(Id, Collection, name ?? Name, tiers ?? Tiers, entries ?? Entries, placements ?? Placements);
}

/// <summary>A reviewed board membership/presentation update. Declining removals cancels the whole update.</summary>
public sealed record BoardUpdateProposal(ImmutableArray<LiveChartId> AdditionCandidates, ImmutableArray<LiveChartId> RemovalCandidates);

public enum PresentationUpdate
{
    None,

    /// <summary>Fill absent or invalid cover pins from valid collection covers; titles and valid pins are unchanged.</summary>
    FillMissingCovers,

    /// <summary>Re-pin every title and cover from the collection, as shown in the preview.</summary>
    FullRefresh,
}

public static class BoardOperations
{
    public static ImmutableArray<Tier> DefaultTiers() =>
    [
        new Tier(TierId.New(), "S", new TierColor(0xFF, 0x7F, 0x7F)),
        new Tier(TierId.New(), "A", new TierColor(0xFF, 0xBF, 0x7F)),
        new Tier(TierId.New(), "B", new TierColor(0xFF, 0xDF, 0x7F)),
        new Tier(TierId.New(), "C", new TierColor(0xFF, 0xFF, 0x7F)),
        new Tier(TierId.New(), "D", new TierColor(0xBF, 0xFF, 0x7F)),
    ];

    /// <summary>
    /// Creates a board with every included collection entry unranked, in effective collection order,
    /// pinning each entry's resolved title and current cover.
    /// </summary>
    public static BoardState Create(BoardId id, string name, CollectionState collection, Func<CollectionEntry, ResolvedTitle> resolveTitle, Func<AssetHash, bool> isAssetValid, ImmutableArray<Tier> tiers)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(resolveTitle);
        ArgumentNullException.ThrowIfNull(isAssetValid);
        var entries = collection.IncludedEntries.Select(e => Pin(e, resolveTitle, isAssetValid)).ToImmutableArray();
        return new BoardState(id, collection.Id, name, tiers, entries, ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty);
    }

    public static BoardState Rename(BoardState board, string name)
    {
        ArgumentNullException.ThrowIfNull(board);
        return board.With(name: name);
    }

    /// <summary>Places an entry copy in a tier at <paramref name="index"/>, removing its preceding placement in the same step.</summary>
    public static BoardState MoveToTier(BoardState board, BoardEntryKey entry, TierId tier, int index)
    {
        ArgumentNullException.ThrowIfNull(board);
        RequireEntry(board, entry);
        if (!board.Placements.ContainsKey(tier))
        {
            throw new KeyNotFoundException($"Tier {tier} is not on the board.");
        }

        var withoutItem = RemovePlacement(board.Placements, entry);
        var target = withoutItem[tier];
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, target.Length);
        return board.With(placements: withoutItem.SetItem(tier, target.Insert(index, entry)));
    }

    /// <summary>Places copy 1 of <paramref name="item"/> in a tier at <paramref name="index"/>.</summary>
    public static BoardState MoveToTier(BoardState board, LiveChartId item, TierId tier, int index) =>
        MoveToTier(board, new BoardEntryKey(item), tier, index);

    /// <summary>Returns an entry copy to the unranked pool, where it takes its seed-order position.</summary>
    public static BoardState ReturnToUnranked(BoardState board, BoardEntryKey entry)
    {
        ArgumentNullException.ThrowIfNull(board);
        RequireEntry(board, entry);
        return board.With(placements: RemovePlacement(board.Placements, entry));
    }

    /// <summary>Returns copy 1 of <paramref name="item"/> to the unranked pool.</summary>
    public static BoardState ReturnToUnranked(BoardState board, LiveChartId item) =>
        ReturnToUnranked(board, new BoardEntryKey(item));

    /// <summary>
    /// Creates a numbered duplicate copy of <paramref name="item"/> on the board and places it unranked
    /// immediately next to the original (and any lower-numbered copies) in seed order.
    /// </summary>
    public static BoardState DuplicateEntry(BoardState board, LiveChartId item, out BoardEntry created)
    {
        ArgumentNullException.ThrowIfNull(board);
        var copies = board.CopiesOf(item);
        if (copies.IsEmpty)
        {
            throw new KeyNotFoundException($"Item {item} is not on board '{board.Name}'.");
        }

        var used = copies.Select(e => e.CopyNumber).ToHashSet();
        var nextCopy = 2;
        while (used.Contains(nextCopy))
        {
            nextCopy++;
        }

        var template = copies.FirstOrDefault(e => e.CopyNumber == 1) ?? copies[0];
        created = new BoardEntry(item, template.PinnedTitle, template.PinnedCover, nextCopy);

        // Insert directly after the highest-copy-number entry for this item that precedes nextCopy,
        // keeping copies contiguous next to the original in seed order (and therefore in Unranked).
        var insertAt = -1;
        for (var i = 0; i < board.Entries.Length; i++)
        {
            if (board.Entries[i].Item == item && board.Entries[i].CopyNumber < nextCopy)
            {
                insertAt = i + 1;
            }
        }

        if (insertAt < 0)
        {
            insertAt = board.Entries.IndexOf(copies[0]);
        }

        return board.With(entries: board.Entries.Insert(insertAt, created));
    }

    /// <summary>Creates a numbered duplicate copy of <paramref name="item"/> in the unranked pool next to the original.</summary>
    public static BoardState DuplicateEntry(BoardState board, LiveChartId item) =>
        DuplicateEntry(board, item, out _);

    /// <summary>Removes a duplicated copy (<see cref="BoardEntryKey.CopyNumber"/> &gt; 1) and any tier placement it held.</summary>
    public static BoardState RemoveDuplicate(BoardState board, BoardEntryKey entry)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!entry.IsDuplicate)
        {
            throw new InvalidOperationException("The original board entry cannot be removed as a duplicate; use Update board after excluding it from the collection.");
        }

        RequireEntry(board, entry);
        var entries = board.Entries.Where(e => e.Key != entry).ToImmutableArray();
        var placements = RemovePlacement(board.Placements, entry);
        return board.With(entries: entries, placements: placements);
    }

    public static BoardState AddTier(BoardState board, Tier tier, int index)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, board.Tiers.Length);
        return board.With(tiers: board.Tiers.Insert(index, tier), placements: board.Placements.Add(tier.Id, []));
    }

    public static BoardState EditTier(BoardState board, TierId tier, string name, TierColor color)
    {
        ArgumentNullException.ThrowIfNull(board);
        var index = TierIndex(board, tier);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return board.With(tiers: board.Tiers.SetItem(index, board.Tiers[index] with { Name = name.Trim(), Color = color }));
    }

    public static BoardState MoveTier(BoardState board, TierId tier, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(board);
        var index = TierIndex(board, tier);
        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, board.Tiers.Length);
        var moved = board.Tiers[index];
        return board.With(tiers: board.Tiers.RemoveAt(index).Insert(newIndex, moved));
    }

    /// <summary>Deletes a tier; its entries return to the unranked pool in the same operation.</summary>
    public static BoardState DeleteTier(BoardState board, TierId tier)
    {
        ArgumentNullException.ThrowIfNull(board);
        var index = TierIndex(board, tier);
        return board.With(tiers: board.Tiers.RemoveAt(index), placements: board.Placements.Remove(tier));
    }

    /// <summary>
    /// Addition candidates are included collection entries not on the board, in effective collection order.
    /// Removal candidates are unique items on the board whose collection entry is excluded or no longer exists.
    /// </summary>
    public static BoardUpdateProposal ProposeUpdate(BoardState board, CollectionState collection)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(collection);
        RequireSourceCollection(board, collection);
        var onBoard = board.Entries.Select(e => e.Item).ToHashSet();
        var additions = collection.IncludedEntries.Where(e => !onBoard.Contains(e.Item)).Select(e => e.Item).ToImmutableArray();
        var included = collection.IncludedEntries.Select(e => e.Item).ToHashSet();
        var removals = board.Entries.Select(e => e.Item).Distinct().Where(item => !included.Contains(item)).ToImmutableArray();
        return new BoardUpdateProposal(additions, removals);
    }

    /// <summary>
    /// Applies a reviewed update as one operation. Retained entries (including duplicates) keep relative seed order;
    /// additions append in effective collection order and start unranked; removed items lose all copies and placements.
    /// </summary>
    public static BoardState ApplyUpdate(
        BoardState board,
        CollectionState collection,
        IReadOnlyCollection<LiveChartId> additions,
        IReadOnlyCollection<LiveChartId> removals,
        PresentationUpdate presentation,
        Func<CollectionEntry, ResolvedTitle> resolveTitle,
        Func<AssetHash, bool> isAssetValid)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(removals);
        ArgumentNullException.ThrowIfNull(resolveTitle);
        ArgumentNullException.ThrowIfNull(isAssetValid);
        RequireSourceCollection(board, collection);

        var proposal = ProposeUpdate(board, collection);
        var add = additions.ToHashSet();
        var remove = removals.ToHashSet();
        if (!add.IsSubsetOf(proposal.AdditionCandidates) || !remove.IsSubsetOf(proposal.RemovalCandidates))
        {
            throw new InvalidOperationException("Board updates may only use the reviewed addition and removal candidates.");
        }

        var retained = board.Entries.Where(e => !remove.Contains(e.Item));
        var appended = collection.Entries.Where(e => add.Contains(e.Item)).Select(e => Pin(e, resolveTitle, isAssetValid));
        var entries = retained.Concat(appended).ToImmutableArray();
        var placements = board.Placements.ToImmutableDictionary(p => p.Key, p => p.Value.Where(k => !remove.Contains(k.Item)).ToImmutableArray());
        var updated = board.With(entries: entries, placements: placements);
        return UpdatePresentation(updated, collection, presentation, resolveTitle, isAssetValid);
    }

    public static BoardState UpdatePresentation(BoardState board, CollectionState collection, PresentationUpdate presentation, Func<CollectionEntry, ResolvedTitle> resolveTitle, Func<AssetHash, bool> isAssetValid)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(resolveTitle);
        ArgumentNullException.ThrowIfNull(isAssetValid);
        RequireSourceCollection(board, collection);
        if (presentation == PresentationUpdate.None)
        {
            return board;
        }

        var entries = board.Entries.Select(entry =>
        {
            var index = collection.IndexOf(entry.Item);
            if (index < 0)
            {
                return entry;
            }

            var source = collection.Entries[index];
            var validCollectionCover = source.Cover is { } c && isAssetValid(c.Asset) ? c.Asset : (AssetHash?)null;
            return presentation switch
            {
                PresentationUpdate.FullRefresh => entry with { PinnedTitle = resolveTitle(source).Text, PinnedCover = validCollectionCover },
                PresentationUpdate.FillMissingCovers when entry.PinnedCover is null || !isAssetValid(entry.PinnedCover.Value) =>
                    entry with { PinnedCover = validCollectionCover },
                _ => entry,
            };
        }).ToImmutableArray();

        return board.With(entries: entries);
    }

    private static BoardEntry Pin(CollectionEntry entry, Func<CollectionEntry, ResolvedTitle> resolveTitle, Func<AssetHash, bool> isAssetValid)
    {
        var cover = entry.Cover is { } c && isAssetValid(c.Asset) ? c.Asset : (AssetHash?)null;
        return new BoardEntry(entry.Item, resolveTitle(entry).Text, cover);
    }

    private static ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>> RemovePlacement(ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>> placements, BoardEntryKey entry)
    {
        foreach (var (tier, items) in placements)
        {
            if (items.Contains(entry))
            {
                return placements.SetItem(tier, items.Remove(entry));
            }
        }

        return placements;
    }

    private static void RequireEntry(BoardState board, BoardEntryKey entry)
    {
        if (!board.Contains(entry))
        {
            throw new KeyNotFoundException($"Entry {entry} is not on board '{board.Name}'.");
        }
    }

    private static int TierIndex(BoardState board, TierId tier)
    {
        for (var i = 0; i < board.Tiers.Length; i++)
        {
            if (board.Tiers[i].Id == tier)
            {
                return i;
            }
        }

        throw new KeyNotFoundException($"Tier {tier} is not on the board.");
    }

    private static void RequireSourceCollection(BoardState board, CollectionState collection)
    {
        if (board.Collection != collection.Id)
        {
            throw new InvalidOperationException("A board can only be updated from its own source collection.");
        }
    }
}
