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

    /// <summary>Retrieves the persisted active packages keyed by package identifier.</summary>
    async Task<IReadOnlyDictionary<string, ActivePackage>> GetActivePackagesAsync(CancellationToken cancellationToken)
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

    /// <summary>Persists updated active versions together with their active packages.</summary>
    Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackage>? activePackages)
        => PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken);

    /// <summary>Persists updated active versions together with package and graph activation metadata.</summary>
    Task PersistActiveVersionsAsync(
        IReadOnlyDictionary<string, string> activeVersions,
        IReadOnlyDictionary<string, string> successfullyApplied,
        string correlationId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ActivePackage>? activePackages,
        IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs)
    {
        if (activeGraphs is { Count: > 0 })
        {
            return Task.FromException(new NotSupportedException("This store registry implementation does not support graph activation metadata."));
        }

        return PersistActiveVersionsAsync(activeVersions, successfullyApplied, correlationId, cancellationToken, activePackages);
    }

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
