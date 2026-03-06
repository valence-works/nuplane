using System.Collections.ObjectModel;

namespace Nuplane.Store.State;

/// <summary>
/// Thread-safe store registry that persists reconciliation state including active versions,
/// last-known-good versions, failure records, and source snapshots. Supports lazy loading
/// from a serialized state file.
/// </summary>
public sealed class StoreRegistry : IStoreRegistry
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IStoreStateSerializer _serializer;
    private readonly string? _stateFilePath;
    private StoreStateRecord _currentState = StoreStateRecord.Empty();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of <see cref="StoreRegistry"/> with a serializer and optional state file path.
    /// </summary>
    public StoreRegistry(IStoreStateSerializer serializer, string? stateFilePath)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _stateFilePath = stateFilePath;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="StoreRegistry"/> with a serializer and options.
    /// </summary>
    public StoreRegistry(IStoreStateSerializer serializer, StoreRegistryOptions options)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        ArgumentNullException.ThrowIfNull(options);
        _stateFilePath = options.StateFilePath;
    }


    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(_currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return new(
                new(_currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastFailureById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
                _currentState.UpdatedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentNullException.ThrowIfNull(successfullyApplied);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var nextActive = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            var nextLkg = new Dictionary<string, string>(_currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase);

            foreach (var (id, version) in successfullyApplied)
            {
                nextLkg[id] = version;
            }

            _currentState = _currentState with
            {
                ActiveVersionById = nextActive,
                LastKnownGoodById = nextLkg,
                UpdatedAt = now
            };

            if (!string.IsNullOrWhiteSpace(_stateFilePath))
            {
                await _serializer.SaveAsync(_stateFilePath, _currentState, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PersistFailureAsync(
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var nextFailures = new Dictionary<string, FailureRecord>(_currentState.LastFailureById, StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId)
            };

            _currentState = _currentState with
            {
                LastFailureById = nextFailures,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(_stateFilePath))
            {
                await _serializer.SaveAsync(_stateFilePath, _currentState, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PersistSourceSnapshotAsync(
        string sourceName,
        SourceSnapshotRef snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(snapshot);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var nextSnapshots = new Dictionary<string, SourceSnapshotRef>(_currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase)
            {
                [sourceName] = snapshot
            };

            _currentState = _currentState with
            {
                LastSuccessfulSourceSnapshots = nextSnapshots,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(_stateFilePath))
            {
                await _serializer.SaveAsync(_stateFilePath, _currentState, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedUnderLockAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_stateFilePath) || _loaded)
        {
            return;
        }

        _currentState = await _serializer.LoadAsync(_stateFilePath, cancellationToken);
        _loaded = true;
    }
}