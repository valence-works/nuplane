using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;

namespace Nuplane.Store.State;

/// <summary>
/// Thread-safe store registry that persists reconciliation state including active versions,
/// last-known-good versions, failure records, and source snapshots. Supports lazy loading
/// from a serialized state file.
/// </summary>
public sealed partial class StoreRegistry : IStoreRegistry, IStoreStateCycleRefresher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IStoreStateSerializer _serializer;
    private readonly string? _stateFilePath;
    private readonly ILogger<StoreRegistry> _logger;
    private readonly EffectiveStorePersistenceSettings? _effectiveSettings;
    private StoreStateRecord _currentState = StoreStateRecord.Empty();
    private bool _loaded;
    private bool _activationLogged;

    /// <summary>
    /// Initializes a new instance of <see cref="StoreRegistry"/> with a serializer and optional state file path.
    /// This constructor preserves low-level test composition semantics where <see langword="null"/>
    /// means in-memory mode.
    /// </summary>
    public StoreRegistry(IStoreStateSerializer serializer, string? stateFilePath)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _stateFilePath = stateFilePath;
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreRegistry>.Instance;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="StoreRegistry"/> with resolved effective persistence settings.
    /// Used by DI-based construction to apply default path behavior and structured activation logging.
    /// </summary>
    public StoreRegistry(
        IStoreStateSerializer serializer,
        EffectiveStorePersistenceSettings effectiveSettings,
        ILogger<StoreRegistry> logger)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _effectiveSettings = effectiveSettings;
        _stateFilePath = effectiveSettings.ResolvedStateFilePath;
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
            return new StoreStateRecord(
                new(_currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastFailureById, StringComparer.OrdinalIgnoreCase),
                new(_currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
                _currentState.UpdatedAt,
                new(_currentState.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase),
                new(_currentState.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase))
            {
                ProtectionRecord = _currentState.ProtectionRecord
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ActivePackageDescriptor>> GetActivePackageDescriptorsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return new ReadOnlyDictionary<string, ActivePackageDescriptor>(
                new Dictionary<string, ActivePackageDescriptor>(_currentState.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<bool> IStoreStateCycleRefresher.RefreshFromDiskAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LogPersistenceActivationOnce();
            if (string.IsNullOrWhiteSpace(_stateFilePath))
            {
                return false;
            }

            var refreshedState = await _serializer.LoadAsync(_stateFilePath, cancellationToken);
            _currentState = refreshedState;
            _loaded = true;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken)
        => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken, activePackageDescriptors: null);

    /// <inheritdoc />
    public async Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors = null)
        => await PersistActiveVersionsAsync(
            activeVersions,
            successfullyApplied,
            correlationId,
            cancellationToken,
            activePackageDescriptors,
            activeGraphs: null);

    /// <inheritdoc />
    public async Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors,
        IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs)
    {
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentNullException.ThrowIfNull(successfullyApplied);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentState = await LoadLatestStateForMutationUnderLockAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var nextActive = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            var nextLkg = new Dictionary<string, string>(currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase);
            var nextDescriptors = activePackageDescriptors is null
                ? new Dictionary<string, ActivePackageDescriptor>(currentState.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ActivePackageDescriptor>(activePackageDescriptors, StringComparer.OrdinalIgnoreCase);
            var nextGraphs = activeGraphs is null
                ? new Dictionary<string, GraphActivationRecord>(currentState.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GraphActivationRecord>(activeGraphs, StringComparer.OrdinalIgnoreCase);

            foreach (var (id, version) in successfullyApplied)
            {
                nextLkg[id] = version;
            }

            foreach (var packageId in nextDescriptors.Keys.ToArray())
            {
                if (!nextActive.ContainsKey(packageId))
                {
                    nextDescriptors.Remove(packageId);
                }
            }

            var nextState = currentState with
            {
                ActiveVersionById = nextActive,
                LastKnownGoodById = nextLkg,
                UpdatedAt = now,
                ActivePackageDescriptorsById = nextDescriptors,
                ActiveGraphsById = nextGraphs
            };

            await CommitStateUnderLockAsync(nextState, cancellationToken);
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
            var currentState = await LoadLatestStateForMutationUnderLockAsync(cancellationToken);

            var nextFailures = new Dictionary<string, FailureRecord>(currentState.LastFailureById, StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId)
            };

            var nextState = currentState with
            {
                LastFailureById = nextFailures,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await CommitStateUnderLockAsync(nextState, cancellationToken);
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
            var currentState = await LoadLatestStateForMutationUnderLockAsync(cancellationToken);

            var nextSnapshots = new Dictionary<string, SourceSnapshotRef>(currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase)
            {
                [sourceName] = snapshot
            };

            var nextState = currentState with
            {
                LastSuccessfulSourceSnapshots = nextSnapshots,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await CommitStateUnderLockAsync(nextState, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedUnderLockAsync(CancellationToken cancellationToken)
    {
        LogPersistenceActivationOnce();

        if (string.IsNullOrWhiteSpace(_stateFilePath) || _loaded)
        {
            return;
        }

        _currentState = await _serializer.LoadAsync(_stateFilePath, cancellationToken);
        _loaded = true;
    }

    private async Task<StoreStateRecord> LoadLatestStateForMutationUnderLockAsync(CancellationToken cancellationToken)
    {
        LogPersistenceActivationOnce();
        return string.IsNullOrWhiteSpace(_stateFilePath)
            ? _currentState
            : await _serializer.LoadAsync(_stateFilePath, cancellationToken);
    }

    private async Task CommitStateUnderLockAsync(StoreStateRecord nextState, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_stateFilePath))
        {
            await _serializer.SaveAsync(_stateFilePath, nextState, cancellationToken);
        }

        _currentState = nextState;
        _loaded = true;
    }

    private void LogPersistenceActivationOnce()
    {
        if (_activationLogged || _effectiveSettings is null)
        {
            return;
        }

        _activationLogged = true;
        LogEffectiveSettings(_effectiveSettings);
    }

    private void LogEffectiveSettings(EffectiveStorePersistenceSettings settings)
    {
        switch (settings.Mode)
        {
            case StorePersistenceMode.DefaultPath:
                LogDefaultPathActivated(_logger, settings.ResolvedStateFilePath!);
                break;
            case StorePersistenceMode.ConfiguredPath:
                LogConfiguredPathActivated(_logger, settings.ResolvedStateFilePath!);
                break;
            case StorePersistenceMode.InMemory:
                LogInMemoryModeActivated(_logger);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Store persistence activated with default path: {StateFilePath}")]
    private static partial void LogDefaultPathActivated(ILogger logger, string stateFilePath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Store persistence activated with configured path: {StateFilePath}")]
    private static partial void LogConfiguredPathActivated(ILogger logger, string stateFilePath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Store persistence is disabled by configuration (UseInMemoryStore=true). Reconciliation state will not survive host restart.")]
    private static partial void LogInMemoryModeActivated(ILogger logger);
}
