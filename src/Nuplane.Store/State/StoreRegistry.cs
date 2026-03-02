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