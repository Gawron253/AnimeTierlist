using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Covers;

public enum CoverCondition
{
    /// <summary>No cover is selected (never acquired, or explicitly marked missing).</summary>
    None,

    /// <summary>The selected cover's file matches its recorded size and hash.</summary>
    Valid,

    /// <summary>The selected cover's file is missing or damaged. The reference is kept until repair or reviewed recovery.</summary>
    Broken,
}

/// <summary>
/// Reviewed recovery for an invalid selected cover. Exact-hash repair happens through cover acquisition; these two
/// undoable user commands are the only ways to change a broken selection. Neither can touch a valid cover.
/// </summary>
public static class CoverRecovery
{
    public static async Task<CoverCondition> InspectAsync(ProjectState state, CollectionId collection, LiveChartId item, IAssetStore assets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(assets);
        if (!TryGetEntry(state, collection, item, out var entry) || entry.Cover is not { } cover)
        {
            return CoverCondition.None;
        }

        return await assets.CheckAsync(state.Assets[cover.Asset], cancellationToken).ConfigureAwait(false) == AssetAvailability.Available
            ? CoverCondition.Valid
            : CoverCondition.Broken;
    }

    /// <summary>
    /// Clears an invalid selection and its provenance to explicit missing state. Needs no network. The session
    /// invalidates pending downloads for the entry, so a late completion cannot undo the decision. Undo restores the
    /// broken reference, not null.
    /// </summary>
    public static async Task<SessionResult> MarkMissingAsync(ProjectSession session, IAssetStore assets, CollectionId collection, LiveChartId item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assets);
        var state = session.Current;
        if (!TryGetEntry(state, collection, item, out var entry))
        {
            return Gone;
        }

        if (entry.Cover is not { } broken)
        {
            return new SessionResult.Rejected("The entry has no selected cover.");
        }

        if (await assets.CheckAsync(state.Assets[broken.Asset], cancellationToken).ConfigureAwait(false) == AssetAvailability.Available)
        {
            return new SessionResult.Rejected("The cover file is valid, so it cannot be marked missing. Only a missing or damaged cover can.");
        }

        return session.Execute("Mark cover missing", s =>
        {
            var current = s.Collection(collection);
            RequireUnchanged(current, item, broken);
            return s.WithCollection(CollectionOperations.MarkCoverMissing(current, item));
        });
    }

    /// <summary>
    /// Replaces an invalid selection with a different validated cover offered by acquisition, recording the new
    /// asset. Refused when the current cover is valid, the selection changed since the offer, or the offered file is
    /// no longer intact. Board pins are not affected.
    /// </summary>
    public static async Task<SessionResult> ReplaceAsync(ProjectSession session, IAssetStore assets, CollectionId collection, LiveChartId item, CoverReplacement replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Provenance.Asset != replacement.Asset.Hash)
        {
            throw new ArgumentException("The replacement's provenance must cite its own asset.", nameof(replacement));
        }

        var state = session.Current;
        if (!TryGetEntry(state, collection, item, out var entry))
        {
            return Gone;
        }

        if (entry.Cover is not { } broken || broken.Occurrence != replacement.Provenance.Occurrence)
        {
            return new SessionResult.Rejected("The entry's cover changed since the new cover was offered. Fetch covers again.");
        }

        if (await assets.CheckAsync(state.Assets[broken.Asset], cancellationToken).ConfigureAwait(false) == AssetAvailability.Available)
        {
            return new SessionResult.Rejected("The current cover file is valid, so recovery cannot replace it.");
        }

        if (await assets.CheckAsync(replacement.Asset, cancellationToken).ConfigureAwait(false) != AssetAvailability.Available)
        {
            return new SessionResult.Rejected("The new cover file is no longer intact. Fetch covers again.");
        }

        return session.Execute("Use new cover", s =>
        {
            var current = s.Collection(collection);
            RequireUnchanged(current, item, broken);
            return s.WithAsset(replacement.Asset)
                .WithCollection(CollectionOperations.ReplaceInvalidCover(current, item, replacement.Provenance, hash => hash != broken.Asset));
        });
    }

    private static SessionResult Gone => new SessionResult.Rejected("The entry is no longer in the collection.");

    private static bool TryGetEntry(ProjectState state, CollectionId collection, LiveChartId item, out CollectionEntry entry)
    {
        entry = null!;
        if (!state.Collections.TryGetValue(collection, out var current) || current.IndexOf(item) < 0)
        {
            return false;
        }

        entry = current.Entry(item);
        return true;
    }

    private static void RequireUnchanged(CollectionState collection, LiveChartId item, CoverProvenance inspected)
    {
        if (collection.Entry(item).Cover != inspected)
        {
            throw new InvalidOperationException("The entry's cover changed while it was being checked. Check it again.");
        }
    }
}
