using System.Collections.ObjectModel;

namespace Nuplane.Store.State;

public sealed class StoreRegistry(StoreStateSerializer serializer, string? stateFilePath)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly StoreStateSerializer serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private StoreStateRecord currentState = StoreStateRecord.Empty();
    private bool loaded;

    public async Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);
            return new(
                new(currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
                new(currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
                new(currentState.LastFailureById, StringComparer.OrdinalIgnoreCase),
                new(currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
                currentState.UpdatedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentNullException.ThrowIfNull(successfullyApplied);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var nextActive = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            var nextLkg = new Dictionary<string, string>(currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase);

            foreach (var (id, version) in successfullyApplied)
            {
                nextLkg[id] = version;
            }

            currentState = currentState with
            {
                ActiveVersionById = nextActive,
                LastKnownGoodById = nextLkg,
                UpdatedAt = now
            };

            if (!string.IsNullOrWhiteSpace(stateFilePath))
            {
                await serializer.SaveAsync(stateFilePath, currentState, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

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

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var nextFailures = new Dictionary<string, FailureRecord>(currentState.LastFailureById, StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new(packageId, stage, message, DateTimeOffset.UtcNow, correlationId)
            };

            currentState = currentState with
            {
                LastFailureById = nextFailures,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(stateFilePath))
            {
                await serializer.SaveAsync(stateFilePath, currentState, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PersistSourceSnapshotAsync(
        string sourceName,
        SourceSnapshotRef snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(snapshot);

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedUnderLockAsync(cancellationToken);

            var nextSnapshots = new Dictionary<string, SourceSnapshotRef>(currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase)
            {
                [sourceName] = snapshot
            };

            currentState = currentState with
            {
                LastSuccessfulSourceSnapshots = nextSnapshots,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(stateFilePath))
            {
                await serializer.SaveAsync(stateFilePath, currentState, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureLoadedUnderLockAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath) || loaded)
        {
            return;
        }

        currentState = await serializer.LoadAsync(stateFilePath, cancellationToken);
        loaded = true;
    }
}