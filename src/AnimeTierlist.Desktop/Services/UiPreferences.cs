using System.Text.Json.Serialization;
using AnimeTierlist.Infrastructure.Portable;

namespace AnimeTierlist.Desktop.Services;

/// <summary>Selectable visual theme modes. Windows High Contrast automatically overrides all three.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>Collection presentation mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CollectionViewMode>))]
public enum CollectionViewMode
{
    Grid,
    List,
}

/// <summary>Poster card density for collection grid and board rows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardDensity>))]
public enum CardDensity
{
    Compact,
    Standard,
    Large,
}

/// <summary>Portable user interface preferences stored outside project databases and archives.</summary>
public sealed record UiSettings
{
    public int SchemaVersion { get; init; } = 1;

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public CollectionViewMode CollectionViewMode { get; init; } = CollectionViewMode.Grid;

    public CardDensity CollectionDensity { get; init; } = CardDensity.Standard;

    public CardDensity BoardDensity { get; init; } = CardDensity.Standard;

    public bool SidebarCollapsed { get; init; }

    public double SidebarWidth { get; init; } = 232;

    public bool BoardTrayCollapsed { get; init; }

    public double BoardTrayHeight { get; init; } = 184;

    public bool CollectionInspectorOpen { get; init; } = true;

    public bool BoardInspectorOpen { get; init; }
}

/// <summary>
/// Manages portable UI settings with tolerant startup loading and strictly serialized, debounced off-dispatcher saves.
/// Save failures are surfaced via <see cref="SaveFailed"/> without blocking the session.
/// </summary>
public sealed class UiPreferencesService : IDisposable
{
    private readonly PortableSettingsStore _store;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _pendingSave;
    private Task _writeTail = Task.CompletedTask;
    private UiSettings _current;
    private long _version;
    private long _savedVersion;
    private bool _disposed;

    /// <summary>Loads existing UI preferences from the resolved data root, or falls back to defaults.</summary>
    public UiPreferencesService(string dataRoot)
    {
        _store = new PortableSettingsStore(dataRoot);
        _current = Sanitize(_store.Load(new UiSettings()));
    }

    /// <summary>Raised whenever settings are updated in memory.</summary>
    public event EventHandler<UiSettings>? Changed;

    /// <summary>Raised if an atomic background settings save fails.</summary>
    public event EventHandler<string>? SaveFailed;

    /// <summary>Current sanitized UI settings.</summary>
    public UiSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies a pure transformation to the current settings and schedules an atomic serialized write.</summary>
    public UiSettings Update(Func<UiSettings, UiSettings> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        UiSettings next;
        CancellationTokenSource saveCts;
        lock (_lock)
        {
            if (_disposed)
            {
                return _current;
            }

            next = Sanitize(transform(_current));
            if (next == _current)
            {
                return _current;
            }

            _current = next;
            _version++;
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = new CancellationTokenSource();
            saveCts = _pendingSave;
        }

        Changed?.Invoke(this, next);
        _ = ScheduleDebouncedFlushAsync(saveCts.Token);
        return next;
    }

    /// <summary>
    /// Cancels any pending debounce timer and flushes the latest settings through the exact same serialized writer
    /// chain so no out-of-order background write can overwrite the final shutdown state.
    /// </summary>
    public void Dispose()
    {
        Task flushTask;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = null;
            flushTask = _savedVersion < _version
                ? EnqueueSerializedWriteLocked(raiseFailure: false)
                : _writeTail;
        }

        try
        {
            flushTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task ScheduleDebouncedFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        Task writeTask;
        lock (_lock)
        {
            if (_disposed || _savedVersion >= _version)
            {
                return;
            }

            writeTask = EnqueueSerializedWriteLocked(raiseFailure: true);
        }

        await writeTask.ConfigureAwait(false);
    }

    /// <summary>Chains one serialized write onto <see cref="_writeTail"/>; caller must hold <see cref="_lock"/>.</summary>
    private Task EnqueueSerializedWriteLocked(bool raiseFailure)
    {
        var previous = _writeTail;
        var next = previous.ContinueWith(
            _ => PersistLatest(raiseFailure),
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
        _writeTail = next;
        return next;
    }

    private void PersistLatest(bool raiseFailure)
    {
        UiSettings snapshot;
        long targetVersion;
        lock (_lock)
        {
            if (_savedVersion >= _version)
            {
                return;
            }

            snapshot = _current;
            targetVersion = _version;
        }

        if (_store.TrySave(snapshot, out var failure))
        {
            lock (_lock)
            {
                _savedVersion = Math.Max(_savedVersion, targetVersion);
            }
        }
        else if (raiseFailure && failure is not null)
        {
            SaveFailed?.Invoke(this, $"UI preferences could not be saved: {failure}");
        }
    }

    private static UiSettings Sanitize(UiSettings raw) =>
        raw with
        {
            Theme = Enum.IsDefined(raw.Theme) ? raw.Theme : AppTheme.Dark,
            CollectionViewMode = Enum.IsDefined(raw.CollectionViewMode) ? raw.CollectionViewMode : CollectionViewMode.Grid,
            CollectionDensity = Enum.IsDefined(raw.CollectionDensity) ? raw.CollectionDensity : CardDensity.Standard,
            BoardDensity = Enum.IsDefined(raw.BoardDensity) ? raw.BoardDensity : CardDensity.Standard,
            SidebarWidth = double.IsFinite(raw.SidebarWidth) ? Math.Clamp(raw.SidebarWidth, 180, 360) : 232,
            BoardTrayHeight = double.IsFinite(raw.BoardTrayHeight) ? Math.Clamp(raw.BoardTrayHeight, 74, 420) : 184,
        };
}
