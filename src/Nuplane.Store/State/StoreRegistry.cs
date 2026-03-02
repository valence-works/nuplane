using System.Collections.ObjectModel;

namespace Nuplane.Store.State;

public sealed class StoreRegistry
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly StoreStateSerializer serializer;
    private readonly string? stateFilePath;
    private StoreStateRecord currentState;

    public StoreRegistry(StoreStateSerializer serializer, string? stateFilePath)
    {
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.stateFilePath = stateFilePath;
        currentState = StoreStateRecord.Empty();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase));
    }

    public async Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return new StoreStateRecord(
            new Dictionary<string, string>(currentState.ActiveVersionById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(currentState.LastKnownGoodById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(currentState.LastFailureById, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(currentState.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
            currentState.UpdatedAt);
    }

    public async Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var nextActive = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            var nextLkg = new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);

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
            var nextFailures = new Dictionary<string, FailureRecord>(currentState.LastFailureById, StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new FailureRecord(packageId, stage, message, DateTimeOffset.UtcNow, correlationId)
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

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            currentState = await serializer.LoadAsync(stateFilePath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}