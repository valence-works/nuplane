using Nuplane.Abstractions;

namespace Nuplane.Store.State;

/// <summary>
/// Defines the contract for persisting and retrieving reconciliation state, including
/// active versions, last-known-good versions, failure records, and source snapshots.
/// </summary>
public interface IStoreRegistry
{
    /// <summary>Retrieves the currently active package versions.</summary>
    Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken);

    /// <summary>Retrieves the complete store state record.</summary>
    Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Retrieves the persisted active package descriptors keyed by package identifier.</summary>
    async Task<IReadOnlyDictionary<string, ActivePackageDescriptor>> GetActivePackageDescriptorsAsync(CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        return state.ActivePackageDescriptorsByIdNormalized;
    }

    /// <summary>Persists updated active versions and marks successfully applied versions as last-known-good.</summary>
    Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Persists updated active versions together with their active package descriptors.</summary>
    Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors)
        => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken);

    /// <summary>Persists updated active versions together with package and graph activation metadata.</summary>
    Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors,
        IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs)
        => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken, activePackageDescriptors);

    /// <summary>Persists a failure record for a package.</summary>
    Task PersistFailureAsync(
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Persists a desired-state source snapshot.</summary>
    Task PersistSourceSnapshotAsync(
        string sourceName,
        SourceSnapshotRef snapshot,
        CancellationToken cancellationToken);
}
