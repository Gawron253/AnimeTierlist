using System.Collections.Immutable;

namespace AnimeTierlist.Core;

public readonly record struct CollectionId(Guid Value)
{
    public static CollectionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>SHA-256 of a validated original image, as 64 lowercase hexadecimal characters.</summary>
public readonly record struct AssetHash
{
    public AssetHash(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("An asset hash is 64 lowercase hexadecimal characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public override string ToString() => Value;
}

/// <summary>
/// Where a selected cover came from. The selected asset is this provenance's <see cref="Asset"/>,
/// so the recorded hash always equals the selected asset hash.
/// </summary>
public sealed record CoverProvenance(OccurrenceRef Occurrence, Uri RequestedUrl, Uri FinalUrl, string RequestContext, AssetHash Asset);

/// <summary>
/// One item in a collection. <see cref="Cover"/> null means no acquired cover is selected
/// (never acquired, or explicitly marked missing).
/// </summary>
public sealed record CollectionEntry(
    LiveChartId Item,
    bool Included,
    OccurrenceRef AcceptedOccurrence,
    bool SourceMissing,
    CoverProvenance? Cover,
    string? TitleOverride);

/// <summary>A collection's complete curated state. Entry order is the effective collection order.</summary>
public sealed record CollectionState
{
    public CollectionState(CollectionId id, string name, AcquisitionContext context, SnapshotId appliedSnapshot, TitleLanguage language, ImmutableArray<CollectionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(context);
        if (entries.IsDefault)
        {
            throw new ArgumentException("Entries must be initialized.", nameof(entries));
        }

        var seen = new HashSet<LiveChartId>();
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Item))
            {
                throw new ArgumentException($"Item {entry.Item} appears more than once in the collection.", nameof(entries));
            }

            if (!entry.SourceMissing && entry.AcceptedOccurrence.Snapshot != appliedSnapshot)
            {
                throw new ArgumentException($"Observed item {entry.Item} must use an occurrence from the applied snapshot.", nameof(entries));
            }
        }

        Id = id;
        Name = name.Trim();
        Context = context;
        AppliedSnapshot = appliedSnapshot;
        Language = language;
        Entries = entries;
    }

    public CollectionId Id { get; }

    public string Name { get; }

    public AcquisitionContext Context { get; }

    public SnapshotId AppliedSnapshot { get; }

    public TitleLanguage Language { get; }

    public ImmutableArray<CollectionEntry> Entries { get; }

    public IEnumerable<CollectionEntry> IncludedEntries => Entries.Where(e => e.Included);

    public int IndexOf(LiveChartId item)
    {
        for (var i = 0; i < Entries.Length; i++)
        {
            if (Entries[i].Item == item)
            {
                return i;
            }
        }

        return -1;
    }

    public CollectionEntry Entry(LiveChartId item)
    {
        var index = IndexOf(item);
        return index >= 0 ? Entries[index] : throw new KeyNotFoundException($"Item {item} is not in collection '{Name}'.");
    }

    internal CollectionState WithEntries(ImmutableArray<CollectionEntry> entries) =>
        new(Id, Name, Context, AppliedSnapshot, Language, entries);

    internal CollectionState With(string? name = null, TitleLanguage? language = null, SnapshotId? appliedSnapshot = null, ImmutableArray<CollectionEntry>? entries = null) =>
        new(Id, name ?? Name, Context, appliedSnapshot ?? AppliedSnapshot, language ?? Language, entries ?? Entries);
}

/// <summary>Reviewable difference between a collection and a candidate snapshot from the same context.</summary>
public sealed record RefreshPreview(
    ImmutableArray<LiveChartId> Added,
    ImmutableArray<LiveChartId> NewlyMissing,
    ImmutableArray<LiveChartId> Reappeared,
    ImmutableArray<TitleChange> TitleChanges,
    bool SourceOrderDiffers,
    ImmutableArray<LiveChartId> CoverCandidateChanges)
{
    /// <summary>True when applying the candidate would change nothing the user can see or acquire.</summary>
    public bool IsEmpty => Added.IsEmpty && NewlyMissing.IsEmpty && Reappeared.IsEmpty && TitleChanges.IsEmpty && !SourceOrderDiffers && CoverCandidateChanges.IsEmpty;
}

public sealed record TitleChange(LiveChartId Item, SourceTitles Before, SourceTitles After);

public static class CollectionOperations
{
    /// <summary>Initial collection from a snapshot: source order, every entry included until the user excludes it.</summary>
    public static CollectionState Create(CollectionId id, string name, SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = snapshot.Sequence
            .Select(s => new CollectionEntry(s.Item, Included: true, s.FirstOccurrence, SourceMissing: false, Cover: null, TitleOverride: null))
            .ToImmutableArray();
        return new CollectionState(id, name, snapshot.Context, snapshot.Id, TitleLanguage.SourceDefault, entries);
    }

    public static CollectionState Rename(CollectionState state, string name)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.With(name: name);
    }

    public static CollectionState SetLanguage(CollectionState state, TitleLanguage language)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Enum.IsDefined(language) ? state.With(language: language) : throw new ArgumentOutOfRangeException(nameof(language));
    }

    public static CollectionState SetIncluded(CollectionState state, IReadOnlyCollection<LiveChartId> items, bool included)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(items);
        var targets = items.ToHashSet();
        var missing = targets.Where(t => state.IndexOf(t) < 0).ToList();
        if (missing.Count > 0)
        {
            throw new KeyNotFoundException($"Items not in collection: {string.Join(", ", missing)}.");
        }

        return state.WithEntries(state.Entries.Select(e => targets.Contains(e.Item) ? e with { Included = included } : e).ToImmutableArray());
    }

    public static CollectionState SetAllIncluded(CollectionState state, bool included)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.WithEntries(state.Entries.Select(e => e with { Included = included }).ToImmutableArray());
    }

    /// <summary>Moves one entry to a new position in the effective order.</summary>
    public static CollectionState Move(CollectionState state, LiveChartId item, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var from = state.IndexOf(item);
        if (from < 0)
        {
            throw new KeyNotFoundException($"Item {item} is not in the collection.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(newIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(newIndex, state.Entries.Length);
        var entry = state.Entries[from];
        return state.WithEntries(state.Entries.RemoveAt(from).Insert(newIndex, entry));
    }

    public static CollectionState SetTitleOverride(CollectionState state, LiveChartId item, string? title)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.IndexOf(item);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Item {item} is not in the collection.");
        }

        var normalized = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return state.WithEntries(state.Entries.SetItem(index, state.Entries[index] with { TitleOverride = normalized }));
    }

    /// <summary>
    /// Attaches a completed download only when the entry has no selected cover and the download was for the
    /// entry's current accepted occurrence. A stale download never attaches to a different observation.
    /// </summary>
    public static bool TryAttachDownloadedCover(CollectionState state, LiveChartId item, CoverProvenance provenance, out CollectionState updated)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(provenance);
        updated = state;
        var index = state.IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        var entry = state.Entries[index];
        if (entry.Cover is not null || entry.AcceptedOccurrence != provenance.Occurrence)
        {
            return false;
        }

        updated = state.WithEntries(state.Entries.SetItem(index, entry with { Cover = provenance }));
        return true;
    }

    /// <summary>Reviewed recovery: clears an invalid selection to explicit missing state. Source identity and curation are unchanged.</summary>
    public static CollectionState MarkCoverMissing(CollectionState state, LiveChartId item)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = state.IndexOf(item);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Item {item} is not in the collection.");
        }

        return state.WithEntries(state.Entries.SetItem(index, state.Entries[index] with { Cover = null }));
    }

    /// <summary>Reviewed recovery: replaces an invalid selection with a different validated cover for the entry's own item.</summary>
    public static CollectionState ReplaceInvalidCover(CollectionState state, LiveChartId item, CoverProvenance replacement, Func<AssetHash, bool> isAssetValid)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(isAssetValid);
        var index = state.IndexOf(item);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Item {item} is not in the collection.");
        }

        var entry = state.Entries[index];
        if (entry.Cover is { } current && isAssetValid(current.Asset))
        {
            throw new InvalidOperationException("A valid selected cover cannot be replaced by recovery.");
        }

        return state.WithEntries(state.Entries.SetItem(index, entry with { Cover = replacement }));
    }

    /// <summary>Describes what applying <paramref name="candidate"/> would change, without changing anything.</summary>
    public static RefreshPreview PreviewRefresh(CollectionState state, SourceSnapshot candidate, Func<OccurrenceRef, SourceOccurrence> observed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(observed);
        RequireSameContext(state, candidate);

        var present = state.Entries.Select(e => e.Item).ToHashSet();
        var added = candidate.Sequence.Where(s => !present.Contains(s.Item)).Select(s => s.Item).ToImmutableArray();
        var newlyMissing = state.Entries.Where(e => !e.SourceMissing && !candidate.Contains(e.Item)).Select(e => e.Item).ToImmutableArray();
        var reappeared = state.Entries.Where(e => e.SourceMissing && candidate.Contains(e.Item)).Select(e => e.Item).ToImmutableArray();

        var titleChanges = ImmutableArray.CreateBuilder<TitleChange>();
        var candidateChanges = ImmutableArray.CreateBuilder<LiveChartId>();
        foreach (var entry in state.Entries)
        {
            if (candidate.TryGetFirstOccurrence(entry.Item, out var next))
            {
                var current = observed(entry.AcceptedOccurrence);
                var latest = candidate.Occurrence(next);
                if (current.Titles != latest.Titles)
                {
                    titleChanges.Add(new TitleChange(entry.Item, current.Titles, latest.Titles));
                }

                // New or changed image candidates matter to entries that still need a cover; selected covers keep
                // their provenance when the refresh is applied.
                if (!current.ImageCandidates.SequenceEqual(latest.ImageCandidates))
                {
                    candidateChanges.Add(entry.Item);
                }
            }
        }

        var currentObservedOrder = state.Entries.Where(e => candidate.Contains(e.Item)).Select(e => e.Item);
        var candidateOrderOfExisting = candidate.Sequence.Where(s => present.Contains(s.Item)).Select(s => s.Item);
        var orderDiffers = !currentObservedOrder.SequenceEqual(candidateOrderOfExisting) || added.Length > 0;

        return new RefreshPreview(added, newlyMissing, reappeared, titleChanges.ToImmutable(), orderDiffers, candidateChanges.ToImmutable());
    }

    /// <summary>
    /// Applies a reviewed refresh as one operation. Inclusion, overrides, and selected covers are preserved.
    /// New items append in source order and start excluded; missing items keep their state with a flag.
    /// With <paramref name="adoptSourceOrder"/>, present items follow the snapshot and missing items follow them in prior relative order.
    /// </summary>
    public static CollectionState ApplyRefresh(CollectionState state, SourceSnapshot candidate, bool adoptSourceOrder)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidate);
        RequireSameContext(state, candidate);

        var updatedExisting = state.Entries.Select(entry =>
            candidate.TryGetFirstOccurrence(entry.Item, out var first)
                ? entry with { AcceptedOccurrence = first, SourceMissing = false }
                : entry with { SourceMissing = true }).ToList();

        var existing = updatedExisting.Select(e => e.Item).ToHashSet();
        var appended = candidate.Sequence
            .Where(s => !existing.Contains(s.Item))
            .Select(s => new CollectionEntry(s.Item, Included: false, s.FirstOccurrence, SourceMissing: false, Cover: null, TitleOverride: null))
            .ToList();

        IEnumerable<CollectionEntry> ordered;
        if (adoptSourceOrder)
        {
            var byItem = updatedExisting.Concat(appended).ToDictionary(e => e.Item);
            ordered = candidate.Sequence.Select(s => byItem[s.Item]).Concat(updatedExisting.Where(e => e.SourceMissing));
        }
        else
        {
            ordered = updatedExisting.Concat(appended);
        }

        return state.With(appliedSnapshot: candidate.Id, entries: ordered.ToImmutableArray());
    }

    private static void RequireSameContext(CollectionState state, SourceSnapshot candidate)
    {
        if (state.Context != candidate.Context)
        {
            throw new InvalidOperationException("A refresh must come from the collection's own acquisition context; another context creates another collection.");
        }
    }
}
