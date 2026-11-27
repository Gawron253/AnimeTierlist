using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Infrastructure.Archives;
using AnimeTierlist.Infrastructure.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>
/// One open project: save state, project-wide undo/redo, failed-save retry/discard, sidebar navigation,
/// and the collection/board views. Session changes may arrive on the background writer thread and are applied
/// on the dispatcher.
/// </summary>
public sealed partial class ProjectViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<CollectionId, CollectionViewModel> _collectionViews = [];
    private readonly Dictionary<BoardId, BoardViewModel> _boardViews = [];
    private readonly Dictionary<LiveChartId, AnimeExtraDetails> _pendingItemExtras = [];
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private int _refreshQueued;
    private Task? _activeRecoveryTask;

    [ObservableProperty]
    private CollectionItem? _selectedCollection;

    [ObservableProperty]
    private BoardItem? _selectedBoard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    private object? _content;

    [ObservableProperty]
    private string _saveText = "Saved";

    [ObservableProperty]
    private string _saveFailureDetail = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditsSuspended), nameof(EditsEnabled), nameof(CanRecover))]
    [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand), nameof(DiscardChangesCommand), nameof(ConfirmDiscardChangesCommand), nameof(UndoCommand), nameof(RedoCommand), nameof(ExportProjectCommand), nameof(NewCollectionCommand), nameof(RenameCollectionCommand), nameof(DeleteCollectionCommand), nameof(MoveCollectionUpCommand), nameof(MoveCollectionDownCommand), nameof(RenameBoardCommand), nameof(DeleteBoardCommand), nameof(MoveBoardUpCommand), nameof(MoveBoardDownCommand))]
    private bool _saveFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoAccessibleName))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private string? _undoLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RedoAccessibleName))]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private string? _redoLabel;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _sidebarCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditsSuspended), nameof(EditsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(NewCollectionCommand), nameof(UndoCommand), nameof(RedoCommand), nameof(ExportProjectCommand), nameof(RenameCollectionCommand), nameof(DeleteCollectionCommand), nameof(MoveCollectionUpCommand), nameof(MoveCollectionDownCommand), nameof(RenameBoardCommand), nameof(DeleteBoardCommand), nameof(MoveBoardUpCommand), nameof(MoveBoardDownCommand))]
    private bool _sessionSuspended;

    [ObservableProperty]
    private double _sidebarWidth = 232;

    /// <summary>Creates a project workspace view model for an open session and store.</summary>
    public ProjectViewModel(string name, ProjectSession session, AppServices app, ProjectServices services, SqliteProjectStore store)
    {
        Store = store;
        _lifetime = new CancellationTokenSource();
        _lifetimeToken = _lifetime.Token;
        Name = name;
        Session = session;
        App = app;
        Services = services;
        _sidebarCollapsed = app.Preferences.Current.SidebarCollapsed;
        _sidebarWidth = app.Preferences.Current.SidebarWidth;
        Acquisition = new AcquisitionViewModel(this);
        Session.Changed += OnSessionChanged;
        Refresh();
        if (Collections.Count > 0)
        {
            SelectedCollection = Collections[0];
        }
        else
        {
            Content = Acquisition;
        }
    }

    public string Name { get; }

    public bool EditsSuspended => SaveFailed || SessionSuspended;

    public bool EditsEnabled => !EditsSuspended;

    /// <summary>Explicit accessible name for the composite Undo button.</summary>
    public string UndoAccessibleName => string.IsNullOrWhiteSpace(UndoLabel) ? "Undo" : $"Undo {UndoLabel}";

    /// <summary>Explicit accessible name for the composite Redo button.</summary>
    public string RedoAccessibleName => string.IsNullOrWhiteSpace(RedoLabel) ? "Redo" : $"Redo {RedoLabel}";

    public string Breadcrumb => Content switch
    {
        CollectionViewModel collection => collection.Name,
        BoardViewModel board => string.IsNullOrWhiteSpace(board.SourceCollectionName) ? board.Name : $"{board.SourceCollectionName}  /  {board.Name}",
        AcquisitionViewModel => "New collection",
        _ => string.Empty,
    };

    public ObservableCollection<CollectionItem> Collections { get; } = [];

    public ObservableCollection<BoardItem> Boards { get; } = [];

    public AcquisitionViewModel Acquisition { get; }

    internal ProjectSession Session { get; }

    internal AppServices App { get; }

    internal ProjectServices Services { get; }

    internal SqliteProjectStore Store { get; }

    /// <summary>
    /// Cancelled when the project closes. The token is captured at construction, so reading it stays safe after the
    /// source is disposed (it then reports cancellation instead of throwing).
    /// </summary>
    internal CancellationToken Lifetime => _lifetimeToken;

    /// <summary>Set while one collection fetches details, so overlapping collections never request the same show twice.</summary>
    internal bool IsFetchingDetails { get; set; }

    /// <summary>Runs a user command and reports rejection or suspension in the status line.</summary>
    internal SessionResult Execute(string label, Func<ProjectState, ProjectState> command)
    {
        var result = Session.Execute(label, command);
        Status = result switch
        {
            SessionResult.Rejected rejected => rejected.Reason,
            SessionResult.Suspended => "Changes are suspended because saving failed. Retry the save or discard unsaved changes.",
            _ => string.Empty,
        };
        return result;
    }

    internal void ShowCollection(CollectionId id)
    {
        Refresh();
        SelectedCollection = Collections.FirstOrDefault(c => c.Id == id);
    }

    internal void ShowBoard(BoardId id)
    {
        Refresh();
        SelectedBoard = Boards.FirstOrDefault(b => b.Id == id);
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;
        App.Preferences.Update(s => s with { SidebarCollapsed = SidebarCollapsed });
    }

    [RelayCommand(CanExecute = nameof(EditsEnabled))]
    private void NewCollection()
    {
        SelectedCollection = null;
        SelectedBoard = null;
        Content = Acquisition;
    }

    /// <summary>Prompts for a new name and renames a collection directly from the sidebar context menu.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCollectionItem))]
    private void RenameCollection(CollectionItem? item)
    {
        if (item is null || EditsSuspended || !Session.Current.Collections.TryGetValue(item.Id, out var collection))
        {
            return;
        }

        var next = App.Dialogs?.PromptText(
            "Rename collection",
            $"Enter a new name for '{collection.Name}'.",
            "Collection _name",
            collection.Name,
            "_Rename");
        if (string.IsNullOrWhiteSpace(next) || next.Trim() == collection.Name)
        {
            return;
        }

        if (Execute("Rename collection", s => s.WithCollection(CollectionOperations.Rename(s.Collection(item.Id), next))) is SessionResult.Accepted)
        {
            Refresh();
        }
    }

    /// <summary>Deletes a collection from the sidebar context menu, blocking if any board still references it.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCollectionItem))]
    private void DeleteCollection(CollectionItem? item)
    {
        if (item is null || EditsSuspended || !Session.Current.Collections.TryGetValue(item.Id, out var collection))
        {
            return;
        }

        var referencingBoards = Session.Current.Boards.Values
            .Where(b => b.Collection == item.Id)
            .Select(b => b.Name)
            .Order(StringComparer.CurrentCulture)
            .ToList();
        if (referencingBoards.Count > 0)
        {
            var reason = $"'{collection.Name}' cannot be deleted while these boards use it: {string.Join(", ", referencingBoards)}. Delete those boards first.";
            Status = reason;
            App.Dialogs?.Inform("Cannot delete collection", reason);
            return;
        }

        if (App.Dialogs?.Confirm("Delete collection",
                $"Delete the collection '{collection.Name}' ({collection.Entries.Length} shows)? Downloaded covers stay in the project. You can undo this until the project is closed.") != true)
        {
            return;
        }

        if (Execute($"Delete collection '{collection.Name}'", s => s.WithoutCollection(item.Id)) is SessionResult.Accepted)
        {
            Status = $"Deleted '{collection.Name}'. Use Undo to restore it.";
            Refresh();
        }
    }

    /// <summary>Moves a sidebar collection one slot up as one undoable command.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveCollectionUp))]
    private void MoveCollectionUp(CollectionItem? item)
    {
        if (item is not null)
        {
            var index = Collections.IndexOf(item);
            if (index > 0)
            {
                MoveCollectionTo(item, index - 1);
            }
        }
    }

    /// <summary>Moves a sidebar collection one slot down as one undoable command.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveCollectionDown))]
    private void MoveCollectionDown(CollectionItem? item)
    {
        if (item is not null)
        {
            var index = Collections.IndexOf(item);
            if (index >= 0 && index < Collections.Count - 1)
            {
                MoveCollectionTo(item, index + 1);
            }
        }
    }

    /// <summary>Drag-and-drop reorder for collections in the left sidebar.</summary>
    public bool MoveCollection(CollectionItem item, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (EditsSuspended)
        {
            return false;
        }

        var from = Collections.IndexOf(item);
        if (from < 0)
        {
            return false;
        }

        var destination = from < insertionIndex ? insertionIndex - 1 : insertionIndex;
        destination = Math.Clamp(destination, 0, Collections.Count - 1);
        return MoveCollectionTo(item, destination);
    }

    private bool MoveCollectionTo(CollectionItem item, int destinationIndex)
    {
        if (EditsSuspended || Collections.IndexOf(item) == destinationIndex)
        {
            return false;
        }

        if (Execute("Reorder collections", s => s.MoveCollection(item.Id, destinationIndex)) is SessionResult.Accepted)
        {
            Refresh();
            return true;
        }

        return false;
    }

    private bool CanEditCollectionItem(CollectionItem? item) => EditsEnabled && item is not null;

    private bool CanMoveCollectionUp(CollectionItem? item) => EditsEnabled && item is not null && Collections.IndexOf(item) > 0;

    private bool CanMoveCollectionDown(CollectionItem? item) => EditsEnabled && item is not null && Collections.IndexOf(item) is >= 0 and var i && i < Collections.Count - 1;

    /// <summary>Prompts for a new name and renames a board directly from the sidebar context menu.</summary>
    [RelayCommand(CanExecute = nameof(CanEditBoardItem))]
    private void RenameBoard(BoardItem? item)
    {
        if (item is null || EditsSuspended || !Session.Current.Boards.TryGetValue(item.Id, out var board))
        {
            return;
        }

        var next = App.Dialogs?.PromptText(
            "Rename board",
            $"Enter a new name for '{board.Name}'.",
            "Board _name",
            board.Name,
            "_Rename");
        if (string.IsNullOrWhiteSpace(next) || next.Trim() == board.Name)
        {
            return;
        }

        if (Execute("Rename board", s => s.WithBoard(BoardOperations.Rename(s.Board(item.Id), next))) is SessionResult.Accepted)
        {
            Refresh();
        }
    }

    /// <summary>Deletes a board directly from the sidebar context menu after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanEditBoardItem))]
    private void DeleteBoard(BoardItem? item)
    {
        if (item is null || EditsSuspended || !Session.Current.Boards.TryGetValue(item.Id, out var board))
        {
            return;
        }

        if (App.Dialogs?.Confirm("Delete board",
                $"Delete the board '{board.Name}'? Its collection and covers are not affected. You can undo this until the project is closed.") != true)
        {
            return;
        }

        if (Execute($"Delete board '{board.Name}'", s => s.WithoutBoard(item.Id)) is SessionResult.Accepted)
        {
            Status = $"Deleted board '{board.Name}'. Use Undo to restore it.";
            Refresh();
        }
    }

    /// <summary>Moves a sidebar board one slot up as one undoable command.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveBoardUp))]
    private void MoveBoardUp(BoardItem? item)
    {
        if (item is not null)
        {
            var index = Boards.IndexOf(item);
            if (index > 0)
            {
                MoveBoardTo(item, index - 1);
            }
        }
    }

    /// <summary>Moves a sidebar board one slot down as one undoable command.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveBoardDown))]
    private void MoveBoardDown(BoardItem? item)
    {
        if (item is not null)
        {
            var index = Boards.IndexOf(item);
            if (index >= 0 && index < Boards.Count - 1)
            {
                MoveBoardTo(item, index + 1);
            }
        }
    }

    /// <summary>Drag-and-drop reorder for boards in the left sidebar.</summary>
    public bool MoveBoard(BoardItem item, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (EditsSuspended)
        {
            return false;
        }

        var from = Boards.IndexOf(item);
        if (from < 0)
        {
            return false;
        }

        var destination = from < insertionIndex ? insertionIndex - 1 : insertionIndex;
        destination = Math.Clamp(destination, 0, Boards.Count - 1);
        return MoveBoardTo(item, destination);
    }

    private bool MoveBoardTo(BoardItem item, int destinationIndex)
    {
        if (EditsSuspended || Boards.IndexOf(item) == destinationIndex)
        {
            return false;
        }

        if (Execute("Reorder boards", s => s.MoveBoard(item.Id, destinationIndex)) is SessionResult.Accepted)
        {
            Refresh();
            return true;
        }

        return false;
    }

    private bool CanEditBoardItem(BoardItem? item) => EditsEnabled && item is not null;

    private bool CanMoveBoardUp(BoardItem? item) => EditsEnabled && item is not null && Boards.IndexOf(item) > 0;

    private bool CanMoveBoardDown(BoardItem? item) => EditsEnabled && item is not null && Boards.IndexOf(item) is >= 0 and var i && i < Boards.Count - 1;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Report(Session.Undo());

    private bool CanUndo() => !EditsSuspended && UndoLabel is not null;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Report(Session.Redo());

    private bool CanRedo() => !EditsSuspended && RedoLabel is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecover))]
    [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand), nameof(DiscardChangesCommand), nameof(ConfirmDiscardChangesCommand))]
    private bool _isRecovering;

    /// <summary>True when a failed save is active and no recovery operation (retry or discard) is currently in flight.</summary>
    public bool CanRecover => SaveFailed && !IsRecovering;

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private Task RetrySaveAsync()
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            return active;
        }

        if (!CanRecover)
        {
            return Task.CompletedTask;
        }

        var task = RunRetrySaveAsync();
        _activeRecoveryTask = task.IsCompleted ? null : task;
        return task;
    }

    /// <summary>Returns any fetched LiveChart extras staged while project saves were suspended.</summary>
    internal AnimeExtraDetails? TryGetPendingItemExtras(LiveChartId item) =>
        _pendingItemExtras.TryGetValue(item, out var extras) ? extras : null;

    /// <summary>True when fetched LiveChart extras for <paramref name="item"/> are waiting for project save recovery.</summary>
    internal bool HasPendingItemExtras(LiveChartId item) => _pendingItemExtras.ContainsKey(item);

    /// <summary>Retains fetched LiveChart extras across dialog close while project saves are suspended.</summary>
    internal void StagePendingItemExtras(AnimeExtraDetails extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        _pendingItemExtras[extras.Item] = extras;
    }

    /// <summary>Removes any staged LiveChart extras for <paramref name="item"/> once accepted or rejected.</summary>
    internal void ClearPendingItemExtras(LiveChartId item) => _pendingItemExtras.Remove(item);

    /// <summary>
    /// Recovers a failed project save (if any) and flushes staged on-demand LiveChart extras so modal dialogs
    /// can complete save recovery without forcing the user to close the dialog first.
    /// </summary>
    internal async Task<bool> RecoverAndFlushPendingItemExtrasAsync()
    {
        Refresh();
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
        }

        if (Session.SaveState == SaveState.Failed)
        {
            var task = RunRetrySaveAsync();
            _activeRecoveryTask = task.IsCompleted ? null : task;
            await task;
        }
        else if (_pendingItemExtras.Count > 0)
        {
            await FlushPendingItemExtrasAsync();
        }
        else
        {
            await Session.FlushAsync();
        }

        Refresh();
        return Session.SaveState == SaveState.Saved && _pendingItemExtras.Count == 0;
    }

    /// <summary>Executes the retry-save recovery workflow while guarding concurrent recovery operations.</summary>
    private async Task RunRetrySaveAsync()
    {
        IsRecovering = true;
        try
        {
            var recovered = await Session.RetryAsync();
            if (recovered)
            {
                recovered = await FlushPendingItemExtrasAsync();
            }

            Status = recovered ? "Saved after retry." : "Saving failed again.";
        }
        catch (ObjectDisposedException)
        {
            Refresh();
        }
        finally
        {
            _activeRecoveryTask = null;
            IsRecovering = false;
        }
    }

    /// <summary>Commits any on-demand LiveChart extras staged while saves were suspended.</summary>
    private async Task<bool> FlushPendingItemExtrasAsync()
    {
        if (_pendingItemExtras.Count == 0)
        {
            return Session.SaveState == SaveState.Saved;
        }

        var snapshot = _pendingItemExtras.Values.ToList();
        foreach (var extras in snapshot)
        {
            var result = Session.RecordItemExtras(extras);
            switch (result)
            {
                case SessionResult.Accepted:
                case SessionResult.Unchanged:
                    if (_pendingItemExtras.TryGetValue(extras.Item, out var current) && ReferenceEquals(current, extras))
                    {
                        _pendingItemExtras.Remove(extras.Item);
                    }

                    break;
                case SessionResult.Suspended:
                    return false;
                case SessionResult.Rejected:
                    if (_pendingItemExtras.TryGetValue(extras.Item, out var rejectedCurrent) && ReferenceEquals(rejectedCurrent, extras))
                    {
                        _pendingItemExtras.Remove(extras.Item);
                    }

                    break;
            }
        }

        await Session.FlushAsync();
        return Session.SaveState == SaveState.Saved;
    }

    /// <summary>Discards queued unsaved changes, reloads the last durable revision, and clears undo history.</summary>
    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task DiscardChangesAsync()
    {
        await DiscardChangesCoreAsync(confirmFirst: false);
    }

    /// <summary>Interactive discard triggered from the UI banner; asks confirmation before clearing undo history.</summary>
    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task ConfirmDiscardChangesAsync()
    {
        await DiscardChangesCoreAsync(confirmFirst: true);
    }

    internal async Task<bool> RetrySaveForCloseAsync()
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
        }

        if (Session.SaveState == SaveState.Failed)
        {
            await RetrySaveAsync();
        }

        return Session.SaveState == SaveState.Saved;
    }

    internal async Task DiscardForCloseAsync()
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
        }

        if (Session.SaveState == SaveState.Failed)
        {
            await DiscardChangesCoreAsync(confirmFirst: false);
        }
    }

    private async Task DiscardChangesCoreAsync(bool confirmFirst)
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
            return;
        }

        if (IsRecovering || Session.SaveState != SaveState.Failed)
        {
            Refresh();
            return;
        }

        if (confirmFirst && App.Dialogs?.Confirm("Discard unsaved changes",
                "Restore the last saved state and discard all unsaved changes? This also clears the session undo history.") != true)
        {
            return;
        }

        if (_activeRecoveryTask is { IsCompleted: false } activeAfterConfirm)
        {
            await activeAfterConfirm;
            return;
        }

        if (Session.SaveState != SaveState.Failed)
        {
            Refresh();
            return;
        }

        var task = RunDiscardAsync();
        _activeRecoveryTask = task.IsCompleted ? null : task;
        await task;
    }

    /// <summary>Executes the discard-and-reload recovery workflow and restores the active collection or board selection.</summary>
    private async Task RunDiscardAsync()
    {
        var previousBoardId = SelectedBoard?.Id;
        var previousCollectionId = SelectedCollection?.Id;

        IsRecovering = true;
        bool discarded;
        try
        {
            discarded = await Session.DiscardAsync(CancellationToken.None);
        }
        catch (InvalidOperationException) when (Session.SaveState != SaveState.Failed)
        {
            // A concurrent retry already resolved the failed save before discard acquired the recovery gate.
            Refresh();
            return;
        }
        catch (Exception ex)
        {
            SaveFailed = Session.SaveState == SaveState.Failed;
            SaveFailureDetail = ex.Message;
            SaveText = SaveFailed ? "Save failed: " + ex.Message : "Saved";
            Status = "Could not reload the last saved project state: " + ex.Message;
            App.Dialogs?.Inform("Could not discard changes",
                "Reloading the last saved state from disk failed. Editing remains paused until the database can be read or the project is closed.",
                [ex.Message]);
            return;
        }
        finally
        {
            _activeRecoveryTask = null;
            IsRecovering = false;
        }

        if (!discarded)
        {
            Refresh();
            return;
        }

        foreach (var view in _collectionViews.Values)
        {
            view.FetchDetailsCancelCommand.Execute(null);
        }

        _collectionViews.Clear();
        _boardViews.Clear();
        _pendingItemExtras.Clear();
        SelectedCollection = null;
        SelectedBoard = null;
        Content = null;
        Refresh();

        if (previousBoardId is { } boardId && Boards.FirstOrDefault(b => b.Id == boardId) is { } restoredBoard)
        {
            SelectedBoard = restoredBoard;
        }
        else if (previousCollectionId is { } collectionId && Collections.FirstOrDefault(c => c.Id == collectionId) is { } restoredCollection)
        {
            SelectedCollection = restoredCollection;
        }
        else
        {
            SelectedCollection = Collections.FirstOrDefault();
            if (SelectedCollection is null)
            {
                Content = Acquisition;
            }
        }

        Status = "Unsaved changes were discarded; the last saved state is restored and undo history was cleared.";
    }

    /// <summary>
    /// Waits for queued saves and returns the saved revision that exports must use, or null (with an explanation)
    /// while a failed save still needs retry or discard.
    /// </summary>
    internal async Task<ProjectState?> SavedRevisionAsync()
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
        }

        await Session.FlushAsync();
        if (Session.SaveState != SaveState.Saved)
        {
            Status = "Export needs a saved project. Retry the failed save or discard unsaved changes first.";
            return null;
        }

        return Session.Durable;
    }

    /// <summary>Exports the saved project as a portable .animerank archive.</summary>
    [RelayCommand(CanExecute = nameof(CanExportProject))]
    private async Task ExportProjectAsync()
    {
        if (App.Dialogs?.ChooseSaveFile($"Export project '{Name}'", "AnimeTierlist project (*.animerank)|*.animerank", Name + ".animerank") is not { } choice)
        {
            return;
        }

        // Checked after the dialog: queued saves are flushed and a failed save blocks the export.
        if (await SavedRevisionAsync() is null)
        {
            return;
        }

        try
        {
            await ProjectArchiveExporter.ExportAsync(Store, choice.Path, choice.ReplaceExisting, ArchiveLimits.Default, Lifetime);
            Status = Session.SaveState == SaveState.Saved
                ? $"Exported the project to {choice.Path}."
                : $"Exported the last saved revision to {choice.Path}; a later change has not been saved.";
        }
        catch (Exception ex) when (ex is ProjectArchiveException or IOException or UnauthorizedAccessException)
        {
            App.Dialogs?.Inform("Project not exported", ex.Message);
            Status = "Project not exported: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanExportProject() => !EditsSuspended;

    /// <summary>Blocks new edits, waits for any in-flight recovery and queued saves, and returns false when a failed save still needs retry or discard.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (_activeRecoveryTask is { IsCompleted: false } active)
        {
            await active;
        }

        var ready = await Session.TryBeginCloseAsync();
        Refresh();
        return ready;
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Session.Changed -= OnSessionChanged;
        try
        {
            await _lifetime.CancelAsync();
            await Session.DisposeAsync();
        }
        finally
        {
            Services.Thumbnails.Dispose();
            _lifetime.Dispose();
        }
    }

    partial void OnSidebarWidthChanged(double value)
    {
        if (double.IsFinite(value) && value >= 180)
        {
            App.Preferences.Update(s => s with { SidebarWidth = value });
        }
    }

    partial void OnSelectedCollectionChanged(CollectionItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedBoard = null;
        UpdateSourceBoardHighlight(null);

        if (!_collectionViews.TryGetValue(value.Id, out var view))
        {
            view = new CollectionViewModel(this, value.Id);
            _collectionViews[value.Id] = view;
        }

        view.Refresh(Session.Current);
        Content = view;
        OnPropertyChanged(nameof(Breadcrumb));
    }

    partial void OnSelectedBoardChanged(BoardItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedCollection = null;
        if (!_boardViews.TryGetValue(value.Id, out var view))
        {
            view = new BoardViewModel(this, value.Id);
            _boardViews[value.Id] = view;
        }

        view.RecheckCovers();
        Content = view;
        UpdateSourceBoardHighlight(view.Collection);
        OnPropertyChanged(nameof(Breadcrumb));
    }

    private void UpdateSourceBoardHighlight(CollectionId? sourceCollectionId)
    {
        foreach (var item in Collections)
        {
            item.IsActiveBoardSource = sourceCollectionId is { } id && item.Id == id;
        }
    }

    private void Report(SessionResult result)
    {
        if (result is SessionResult.Suspended)
        {
            Status = "Changes are suspended because saving failed. Retry the save or discard unsaved changes.";
        }
        else if (result is SessionResult.Rejected rejected)
        {
            Status = rejected.Reason;
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        var state = Session.Current;
        SaveFailed = Session.SaveState == SaveState.Failed;
        SessionSuspended = Session.IsSuspended;
        IsSaving = Session.SaveState == SaveState.Saving;
        SaveFailureDetail = Session.Failure?.Message ?? string.Empty;
        SaveText = Session.SaveState switch
        {
            SaveState.Saved => "Saved",
            SaveState.Saving => "Saving...",
            _ => $"Save failed: {Session.Failure?.Message}",
        };
        UndoLabel = Session.UndoLabel;
        RedoLabel = Session.RedoLabel;
        ConfirmDiscardChangesCommand.NotifyCanExecuteChanged();
        ExportProjectCommand.NotifyCanExecuteChanged();
        Acquisition.NotifyEditsSuspendedChanged();

        var ordered = state.CollectionOrder.Select(id => state.Collections[id]).ToList();
        for (var i = Collections.Count - 1; i >= 0; i--)
        {
            if (!state.Collections.ContainsKey(Collections[i].Id))
            {
                if (_collectionViews.Remove(Collections[i].Id, out var removed))
                {
                    // The removed view held the only Stop control for its details run.
                    removed.FetchDetailsCancelCommand.Execute(null);
                }

                Collections.RemoveAt(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var collection = ordered[i];
            var includedCount = collection.Entries.Count(e => e.Included);
            var badge = $"{includedCount}/{collection.Entries.Length}";
            var label = $"{collection.Name} ({badge})";
            var existing = Collections.FirstOrDefault(c => c.Id == collection.Id);
            if (existing is null)
            {
                Collections.Insert(i, new CollectionItem(collection.Id, label, collection.Name, badge, Describe(collection.Context)));
            }
            else
            {
                existing.Label = label;
                existing.DisplayName = collection.Name;
                existing.CountBadge = badge;
                var from = Collections.IndexOf(existing);
                if (from != i)
                {
                    Collections.Move(from, i);
                }
            }
        }

        var boards = state.BoardOrder.Select(id => state.Boards[id]).ToList();
        for (var i = Boards.Count - 1; i >= 0; i--)
        {
            if (!state.Boards.ContainsKey(Boards[i].Id))
            {
                _boardViews.Remove(Boards[i].Id);
                Boards.RemoveAt(i);
            }
        }

        for (var i = 0; i < boards.Count; i++)
        {
            var board = boards[i];
            var source = state.Collections.TryGetValue(board.Collection, out var c) ? c.Name : string.Empty;
            var ranked = board.Placements.Values.Sum(p => p.Length);
            var progressBadge = $"{ranked}/{board.Entries.Length}";
            var existing = Boards.FirstOrDefault(b => b.Id == board.Id);
            if (existing is null)
            {
                Boards.Insert(i, new BoardItem(board.Id, board.Name, source, progressBadge));
            }
            else
            {
                existing.Label = board.Name;
                existing.Context = source;
                existing.ProgressBadge = progressBadge;
                var from = Boards.IndexOf(existing);
                if (from != i)
                {
                    Boards.Move(from, i);
                }
            }
        }

        MoveCollectionUpCommand.NotifyCanExecuteChanged();
        MoveCollectionDownCommand.NotifyCanExecuteChanged();
        MoveBoardUpCommand.NotifyCanExecuteChanged();
        MoveBoardDownCommand.NotifyCanExecuteChanged();

        if ((SelectedBoard is { } selectedBoard && !state.Boards.ContainsKey(selectedBoard.Id))
            || (Content is BoardViewModel shownBoard && !state.Boards.ContainsKey(shownBoard.Id)))
        {
            var source = (Content as BoardViewModel)?.Collection;
            SelectedBoard = null;
            if (source is { } sourceId && Collections.FirstOrDefault(c => c.Id == sourceId) is { } item)
            {
                SelectedCollection = item;
            }
            else
            {
                Content = Acquisition;
            }
        }

        // A bound list box may already have cleared the selection when the item was removed, so check what is shown.
        if ((SelectedCollection is { } selected && !state.Collections.ContainsKey(selected.Id))
            || (Content is CollectionViewModel shown && !state.Collections.ContainsKey(shown.Id)))
        {
            SelectedCollection = null;
            Content = Acquisition;
        }

        foreach (var view in _collectionViews.Values)
        {
            view.Refresh(state);
        }

        foreach (var view in _boardViews.Values)
        {
            view.Refresh(state);
        }

        UpdateSourceBoardHighlight(Content is BoardViewModel activeBoard ? activeBoard.Collection : null);
        OnPropertyChanged(nameof(Breadcrumb));
    }

    internal static string Describe(AcquisitionContext context) =>
        $"{(context.Season is { } season ? season.ToString() : "Whole year")} {context.Year} · {CategoryLabel(context.Category)}";

    internal static string CategoryLabel(CatalogueCategory category) => category switch
    {
        CatalogueCategory.Tv => "TV",
        CatalogueCategory.Movies => "Movies",
        CatalogueCategory.Ovas => "OVAs",
        _ => "All",
    };
}

/// <summary>Sidebar item representing a ranking board.</summary>
public sealed partial class BoardItem(BoardId id, string label, string context, string progressBadge) : ObservableObject
{
    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private string _context = context;

    [ObservableProperty]
    private string _progressBadge = progressBadge;

    public BoardId Id { get; } = id;
}

/// <summary>Sidebar item representing a collection.</summary>
public sealed partial class CollectionItem(CollectionId id, string label, string displayName, string countBadge, string context) : ObservableObject
{
    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private string _displayName = displayName;

    [ObservableProperty]
    private string _countBadge = countBadge;

    [ObservableProperty]
    private bool _isActiveBoardSource;

    public CollectionId Id { get; } = id;

    public string Context { get; } = context;
}
