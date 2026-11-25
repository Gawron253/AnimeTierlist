using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;

namespace AnimeTierlist.Application.Session;

public enum SaveState
{
    /// <summary>Every accepted edit is durably committed.</summary>
    Saved,

    /// <summary>Accepted edits are queued or being committed.</summary>
    Saving,

    /// <summary>A commit failed. Mutations are suspended until <see cref="ProjectSession.RetryAsync"/> or <see cref="ProjectSession.DiscardAsync"/>.</summary>
    Failed,
}

/// <summary>The outcome of a user command, undo, redo, or download completion.</summary>
public abstract record SessionResult
{
    private SessionResult()
    {
    }

    /// <summary>The change was applied to the current revision and queued for durable commit.</summary>
    public sealed record Accepted : SessionResult;

    /// <summary>The command produced no change; nothing was queued or added to history.</summary>
    public sealed record Unchanged : SessionResult;

    /// <summary>A domain rule rejected the command; nothing changed.</summary>
    public sealed record Rejected(string Reason) : SessionResult;

    /// <summary>A failed save suspends mutations until retry succeeds or discard completes.</summary>
    public sealed record Suspended : SessionResult;

    /// <summary>A download completion no longer matches its entry's current request and was not recorded.</summary>
    public sealed record Stale : SessionResult;
}

/// <summary>
/// Issued when a cover download starts. Completion is accepted only for the same accepted occurrence while no
/// explicit cover decision (such as mark missing) or discard has happened since.
/// </summary>
public sealed record CoverRequest(CollectionId Collection, LiveChartId Item, OccurrenceRef Occurrence, long Epoch);

/// <summary>
/// The open project's working session. Each completed edit gesture becomes one durable commit through a single
/// background writer in order. Undo and redo are ordinary edits of the same kind: their targets pass the same
/// <see cref="ProjectState"/> rules and the same writer. Cover download completion is not a user command; it binds a
/// validated asset only to the entry's matching accepted occurrence and is retained as session evidence so undo and
/// redo preserve it for that occurrence. A failed commit suspends all later mutations until retry or discard.
/// All members are thread-safe. <see cref="Changed"/> may be raised on a background thread.
/// </summary>
public sealed class ProjectSession : IAsyncDisposable
{
    private readonly IProjectStore _store;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly Queue<PendingWrite> _pending = new();
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private readonly Dictionary<EntryKey, List<CoverEvidence>> _evidence = [];
    private readonly Dictionary<EntryKey, long> _coverEpochs = [];

    private ProjectState _current;
    private ProjectState _durable;
    private Exception? _failure;
    private Task _writer = Task.CompletedTask;
    private bool _writerRunning;
    private bool _retrying;
    private bool _closing;
    private bool _disposed;
    private int _inFlightRecoveries;
    private TaskCompletionSource? _recoveriesDrained;
    private Task<bool>? _closePrepTask;
    private Task? _disposeTask;
    private long _generation;

    private ProjectSession(IProjectStore store, ProjectState durable)
    {
        _store = store;
        _current = durable;
        _durable = durable;
    }

    /// <summary>Raised after any change to the current revision, save state, or history availability.</summary>
    public event EventHandler? Changed;

    public ProjectState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The last revision known to be durably committed.</summary>
    public ProjectState Durable
    {
        get
        {
            lock (_gate)
            {
                return _durable;
            }
        }
    }

    public SaveState SaveState
    {
        get
        {
            lock (_gate)
            {
                return _failure is not null ? SaveState.Failed : _pending.Count > 0 ? SaveState.Saving : SaveState.Saved;
            }
        }
    }

    /// <summary>True while mutations are suspended due to a save failure, an in-flight retry, or session closing.</summary>
    public bool IsSuspended
    {
        get
        {
            lock (_gate)
            {
                return _closing || _failure is not null || _retrying;
            }
        }
    }

    /// <summary>The error from the failed commit while <see cref="SaveState"/> is <see cref="SaveState.Failed"/>.</summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public string? UndoLabel
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0 ? _undo[^1].Label : null;
            }
        }
    }

    public string? RedoLabel
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0 ? _redo[^1].Label : null;
            }
        }
    }

    /// <summary>Takes ownership of an open store and loads its saved revision.</summary>
    public static async Task<ProjectSession> OpenAsync(IProjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var data = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectSession(store, ProjectState.FromData(data));
    }

    /// <summary>
    /// Applies one user command as one undoable, durably committed step. Domain rule violations reject the command
    /// without changing anything.
    /// </summary>
    public SessionResult Execute(string label, Func<ProjectState, ProjectState> command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(command);
        SessionResult result;
        lock (_gate)
        {
            if (_closing || _failure is not null || _retrying)
            {
                return new SessionResult.Suspended();
            }

            ProjectState next;
            try
            {
                next = command(_current);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return new SessionResult.Rejected(ex.Message);
            }

            if (ReferenceEquals(next, _current) || SameContent(_current, next))
            {
                return new SessionResult.Unchanged();
            }

            var before = _current;
            result = Apply(next);
            _undo.Add(new HistoryEntry(label, before, next));
            _redo.Clear();
        }

        RaiseChanged();
        return result;
    }

    /// <summary>Restores the revision before the most recent user command, keeping completed downloads that match it.</summary>
    public SessionResult Undo() => Travel(_undo, _redo, entry => entry.Before);

    /// <summary>Reapplies the most recently undone user command, keeping completed downloads that match it.</summary>
    public SessionResult Redo() => Travel(_redo, _undo, entry => entry.After);

    /// <summary>Records the start of a cover download for an entry's current accepted occurrence.</summary>
    public CoverRequest BeginCoverRequest(CollectionId collection, LiveChartId item)
    {
        lock (_gate)
        {
            var entry = _current.Collection(collection).Entry(item);
            return new CoverRequest(collection, item, entry.AcceptedOccurrence, EpochOf(new EntryKey(collection, item)));
        }
    }

    /// <summary>
    /// Completes a download whose bytes are already validated and installed in the asset store. The result is
    /// retained as session evidence for its occurrence. It is selected now only when the entry has no selected
    /// cover and still accepts the same occurrence. This is not an undoable command.
    /// </summary>
    public SessionResult CompleteCoverDownload(CoverRequest request, CoverProvenance provenance, AssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(asset);
        if (provenance.Occurrence != request.Occurrence || provenance.Asset != asset.Hash)
        {
            throw new ArgumentException("The provenance must describe the requested occurrence and the validated asset.", nameof(provenance));
        }

        SessionResult result;
        lock (_gate)
        {
            if (_closing || _failure is not null || _retrying)
            {
                return new SessionResult.Suspended();
            }

            var key = new EntryKey(request.Collection, request.Item);
            if (EpochOf(key) != request.Epoch || !_current.Collections.ContainsKey(request.Collection))
            {
                return new SessionResult.Stale();
            }

            if (!_evidence.TryGetValue(key, out var list))
            {
                list = [];
                _evidence[key] = list;
            }

            list.RemoveAll(e => e.Provenance.Occurrence == provenance.Occurrence);
            list.Add(new CoverEvidence(provenance, asset));

            var next = ApplyEvidence(_current);
            if (ReferenceEquals(next, _current))
            {
                return new SessionResult.Unchanged();
            }

            result = Apply(next);
        }

        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Stores on-demand fetched LiveChart details and video links inside the project without adding a user undo step
    /// or altering collection curation or board placements.
    /// </summary>
    public SessionResult RecordItemExtras(AnimeExtraDetails extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        SessionResult result;
        lock (_gate)
        {
            if (_closing || _failure is not null || _retrying)
            {
                return new SessionResult.Suspended();
            }

            ProjectState next;
            try
            {
                next = _current.WithItemExtras(extras);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return new SessionResult.Rejected(ex.Message);
            }

            if (ReferenceEquals(next, _current))
            {
                return new SessionResult.Unchanged();
            }

            result = Apply(next);
        }

        RaiseChanged();
        return result;
    }

    /// <summary>Retries the failed commit and every queued commit after it. Serialized against <see cref="DiscardAsync"/> and <see cref="DisposeAsync"/>. Returns true when all are saved.</summary>
    public async Task<bool> RetryAsync()
    {
        if (!TryEnterRecovery())
        {
            return false;
        }

        try
        {
            await _recoveryGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_closing)
                    {
                        return false;
                    }

                    if (_failure is null)
                    {
                        return true;
                    }

                    _retrying = true;
                    _failure = null;
                    StartWriter();
                }

                RaiseChanged();
                try
                {
                    await FlushAsync().ConfigureAwait(false);
                    return SaveState == SaveState.Saved;
                }
                finally
                {
                    bool cleared;
                    lock (_gate)
                    {
                        cleared = _retrying;
                        _retrying = false;
                    }

                    if (cleared)
                    {
                        RaiseChanged();
                    }
                }
            }
            finally
            {
                _recoveryGate.Release();
            }
        }
        finally
        {
            ExitRecovery();
        }
    }

    /// <summary>
    /// After a failed commit, drops every queued edit, reloads the last durable revision, and clears all undo/redo
    /// history and download evidence. Serialized against <see cref="RetryAsync"/> and <see cref="DisposeAsync"/> so a
    /// stale snapshot can never overwrite a newer committed retry. Returns true when pending edits were discarded, or
    /// false if a concurrent retry or disposal already resolved or closed the session.
    /// </summary>
    public async Task<bool> DiscardAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closing)
            {
                return false;
            }

            if (_failure is null && _inFlightRecoveries == 0)
            {
                throw new InvalidOperationException("Discard is only available after a failed save.");
            }

            _inFlightRecoveries++;
        }

        try
        {
            await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_closing || _failure is null)
                    {
                        return false;
                    }
                }

                // Reload the durable revision while holding _recoveryGate so no RetryAsync can commit meanwhile,
                // and BEFORE mutating in-memory queues so a failed reload leaves pending edits intact.
                var reloaded = ProjectState.FromData(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));
                lock (_gate)
                {
                    if (_closing || _failure is null)
                    {
                        return false;
                    }

                    _pending.Clear();
                    _undo.Clear();
                    _redo.Clear();
                    _evidence.Clear();
                    _coverEpochs.Clear();
                    _generation++;
                    _current = reloaded;
                    _durable = reloaded;
                    _failure = null;
                }

                RaiseChanged();
                return true;
            }
            finally
            {
                _recoveryGate.Release();
            }
        }
        finally
        {
            ExitRecovery();
        }
    }

    /// <summary>Waits until the writer is idle: every queued edit is committed or a failure suspended the queue.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task writer;
            lock (_gate)
            {
                if (!_writerRunning)
                {
                    return;
                }

                writer = _writer;
            }

            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocks new mutations and recovery calls, waits for any in-flight recovery and queued commits to finish,
    /// and keeps mutations blocked when all edits are durably saved. Overlapping calls share one in-flight close
    /// preparation task so a successful result always guarantees mutations stay blocked through disposal. If a commit
    /// fails during the final flush (or was already failed), releases the close freeze so recovery remains available
    /// and returns false.
    /// </summary>
    public Task<bool> TryBeginCloseAsync()
    {
        TaskCompletionSource<bool>? owner;
        Task? drain = null;
        Task<bool> task;
        lock (_gate)
        {
            if (_closePrepTask is not null)
            {
                return _closePrepTask;
            }

            if (_disposeTask is not null)
            {
                return Task.FromResult(_failure is null && _pending.Count == 0);
            }

            _closing = true;
            if (_inFlightRecoveries > 0)
            {
                _recoveriesDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _recoveriesDrained.Task;
            }

            owner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closePrepTask = owner.Task;
            task = owner.Task;
        }

        RaiseChanged();
        _ = RunBeginCloseAsync(owner, drain);
        return task;
    }

    private async Task RunBeginCloseAsync(TaskCompletionSource<bool> completion, Task? drain)
    {
        try
        {
            if (drain is not null)
            {
                await drain.ConfigureAwait(false);
            }

            await FlushAsync().ConfigureAwait(false);
            lock (_gate)
            {
                var saved = _failure is null && _pending.Count == 0;
                if (!saved && _disposeTask is null)
                {
                    _closing = false;
                    _recoveriesDrained = null;
                    _closePrepTask = null;
                }

                // Complete under _gate (RunContinuationsAsynchronously prevents inline continuations)
                // so a new close cannot start during RaiseChanged while the failed task is still pending.
                completion.TrySetResult(saved);
            }

            RaiseChanged();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_disposeTask is null)
                {
                    _closing = false;
                    _recoveriesDrained = null;
                    _closePrepTask = null;
                }

                completion.TrySetException(ex);
            }

            RaiseChanged();
        }
    }

    /// <summary>
    /// Rejects new mutations and recovery calls, waits for any already-admitted recovery calls and queued commits to finish,
    /// then closes the store outside the session lock. Overlapping and repeated calls share one disposal task and complete
    /// only after cleanup finishes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null;
        Task task;
        lock (_gate)
        {
            _closing = true;
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
            }

            task = _disposeTask;
        }

        if (owner is not null)
        {
            _ = RunDisposeAsync(owner);
        }

        return new ValueTask(task);
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? drain = null;
        lock (_gate)
        {
            if (_inFlightRecoveries > 0)
            {
                _recoveriesDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _recoveriesDrained.Task;
            }
        }

        if (drain is not null)
        {
            await drain.ConfigureAwait(false);
        }

        await FlushAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _disposed = true;
        }

        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Dispose();
        }
    }

    /// <summary>Admits a recovery operation unless disposal has started.</summary>
    private bool TryEnterRecovery()
    {
        lock (_gate)
        {
            if (_closing)
            {
                return false;
            }

            _inFlightRecoveries++;
            return true;
        }
    }

    /// <summary>Decrements admitted recovery count and wakes <see cref="DisposeAsync"/> once all admitted callers exit.</summary>
    private void ExitRecovery()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _inFlightRecoveries--;
            if (_closing && _inFlightRecoveries == 0)
            {
                drained = _recoveriesDrained;
            }
        }

        drained?.TrySetResult();
    }

    private SessionResult Travel(List<HistoryEntry> from, List<HistoryEntry> to, Func<HistoryEntry, ProjectState> pick)
    {
        SessionResult result;
        lock (_gate)
        {
            if (_closing || _failure is not null || _retrying)
            {
                return new SessionResult.Suspended();
            }

            if (from.Count == 0)
            {
                return new SessionResult.Unchanged();
            }

            var entry = from[^1];
            ProjectState target;
            try
            {
                target = Reconcile(pick(entry));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return new SessionResult.Rejected(ex.Message);
            }

            result = Apply(target);
            from.RemoveAt(from.Count - 1);
            to.Add(entry);
        }

        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Re-validates a history revision through the same project rules as forward edits, keeps every snapshot and
    /// asset recorded since, and re-applies retained download evidence for matching occurrences.
    /// </summary>
    private ProjectState Reconcile(ProjectState revision)
    {
        var target = ProjectState.Empty.WithAppendOnlyRecordsOf(_current).WithAppendOnlyRecordsOf(revision);
        target = revision.CollectionOrder.Aggregate(target, (s, id) => s.WithCollection(revision.Collections[id]));
        target = revision.BoardOrder.Aggregate(target, (s, id) => s.WithBoard(revision.Boards[id]));
        target = ApplyEvidence(target);

        // Keep unchanged aggregates as the current instances so only real differences are written.
        foreach (var collection in target.Collections.Values)
        {
            if (_current.Collections.TryGetValue(collection.Id, out var old) && !ReferenceEquals(old, collection) && Same(old, collection))
            {
                target = target.WithCollection(old);
            }
        }

        foreach (var board in target.Boards.Values)
        {
            if (_current.Boards.TryGetValue(board.Id, out var old) && !ReferenceEquals(old, board) && Same(old, board))
            {
                target = target.WithBoard(old);
            }
        }

        return target;
    }

    /// <summary>Selects retained download results for entries with no cover whose accepted occurrence matches.</summary>
    private ProjectState ApplyEvidence(ProjectState state)
    {
        foreach (var (key, list) in _evidence)
        {
            if (!state.Collections.TryGetValue(key.Collection, out var collection) || collection.IndexOf(key.Item) < 0)
            {
                continue;
            }

            var accepted = collection.Entry(key.Item).AcceptedOccurrence;
            var match = list.Find(e => e.Provenance.Occurrence == accepted);
            if (match is not null && CollectionOperations.TryAttachDownloadedCover(collection, key.Item, match.Provenance, out var updated))
            {
                state = state.WithAsset(match.Asset).WithCollection(updated);
            }
        }

        return state;
    }

    /// <summary>Makes <paramref name="next"/> current and queues its commit. Caller holds the gate.</summary>
    private SessionResult.Accepted Apply(ProjectState next)
    {
        InvalidateClearedCovers(_current, next);
        var change = _current.ChangesTo(next);
        _pending.Enqueue(new PendingWrite(change, next));
        _current = next;
        StartWriter();
        return new SessionResult.Accepted();
    }

    /// <summary>
    /// A transition that removes a selected cover while keeping the same accepted occurrence (mark missing, or
    /// undo/redo across it) is an explicit cover decision: in-flight downloads and retained evidence for that entry
    /// can no longer select a cover. Removal because the accepted occurrence changed (undoing a refresh) keeps the
    /// evidence, so redo can restore the matching download.
    /// </summary>
    private void InvalidateClearedCovers(ProjectState before, ProjectState after)
    {
        foreach (var collection in after.Collections.Values)
        {
            if (!before.Collections.TryGetValue(collection.Id, out var old) || ReferenceEquals(old, collection))
            {
                continue;
            }

            foreach (var entry in collection.Entries)
            {
                var index = old.IndexOf(entry.Item);
                var previous = index >= 0 ? old.Entries[index] : null;
                if (previous is { Cover: not null } && entry.Cover is null && previous.AcceptedOccurrence == entry.AcceptedOccurrence)
                {
                    var key = new EntryKey(collection.Id, entry.Item);
                    _evidence.Remove(key);
                    _coverEpochs[key] = EpochOf(key) + 1;
                }
            }
        }
    }

    private long EpochOf(EntryKey key) => (_generation << 32) + _coverEpochs.GetValueOrDefault(key);

    private void StartWriter()
    {
        if (_writerRunning || _failure is not null || _disposed || _pending.Count == 0)
        {
            return;
        }

        _writerRunning = true;
        _writer = Task.Run(WriteQueuedAsync);
    }

    private async Task WriteQueuedAsync()
    {
        while (true)
        {
            PendingWrite next;
            lock (_gate)
            {
                if (_pending.Count == 0 || _failure is not null)
                {
                    _writerRunning = false;
                    return;
                }

                next = _pending.Peek();
            }

            Exception? failure = null;
            try
            {
                if (!next.Change.IsEmpty)
                {
                    await _store.CommitAsync(next.Change, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Any failure leaves the edit unsaved and visible; the queue stays intact for retry or discard.
                failure = ex;
            }

            lock (_gate)
            {
                if (failure is not null)
                {
                    _failure = failure;
                    _writerRunning = false;
                }
                else
                {
                    _pending.Dequeue();
                    _durable = next.Result;
                }
            }

            RaiseChanged();
            if (failure is not null)
            {
                return;
            }
        }
    }

    /// <summary>True when a command produced new instances without changing anything, so it adds no history or write.</summary>
    private static bool SameContent(ProjectState a, ProjectState b) =>
        a.Snapshots.Count == b.Snapshots.Count && a.Snapshots.Keys.All(b.Snapshots.ContainsKey)
        && a.Assets.Count == b.Assets.Count && a.Assets.Keys.All(b.Assets.ContainsKey)
        && a.ItemExtras.Count == b.ItemExtras.Count && a.ItemExtras.All(p => b.ItemExtras.TryGetValue(p.Key, out var other) && p.Value == other)
        && a.CollectionOrder.SequenceEqual(b.CollectionOrder)
        && a.BoardOrder.SequenceEqual(b.BoardOrder)
        && a.Collections.Count == b.Collections.Count && a.Collections.All(p => b.Collections.TryGetValue(p.Key, out var other) && (ReferenceEquals(p.Value, other) || Same(p.Value, other)))
        && a.Boards.Count == b.Boards.Count && a.Boards.All(p => b.Boards.TryGetValue(p.Key, out var other) && (ReferenceEquals(p.Value, other) || Same(p.Value, other)));

    private static bool Same(CollectionState a, CollectionState b) =>
        a.Name == b.Name && a.Context == b.Context && a.AppliedSnapshot == b.AppliedSnapshot && a.Language == b.Language && a.Entries.SequenceEqual(b.Entries);

    private static bool Same(BoardState a, BoardState b) =>
        a.Name == b.Name && a.Collection == b.Collection && a.Tiers.SequenceEqual(b.Tiers) && a.Entries.SequenceEqual(b.Entries) &&
        a.Tiers.All(t => a.Placements[t.Id].SequenceEqual(b.Placements[t.Id]));

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private readonly record struct EntryKey(CollectionId Collection, LiveChartId Item);

    private sealed record CoverEvidence(CoverProvenance Provenance, AssetRecord Asset);

    private sealed record HistoryEntry(string Label, ProjectState Before, ProjectState After);

    private sealed record PendingWrite(ProjectChange Change, ProjectState Result);
}
