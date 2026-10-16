using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Abstractions;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Reconciliation;

/// <summary>Loads only a Core-verified persisted recovery graph after its owner locks have been released.</summary>
/// <remarks>
/// This participant-only handoff keeps the persisted recoverable graph separate from the active-only
/// graph-use publication view. It does not relax validation of ordinary resolved-graph handoffs.
/// </remarks>
internal interface ILeasedPackageGraphRecoveryLoadingObserver : ILeaseBoundPackageGraphLoadingObserver
{
    Task<LeasedPackageGraphLoadingResult> LoadRecoveryAsync(
        LeasedPackageGraphRecoveryLoadingHandoff handoff,
        CancellationToken cancellationToken);
}

/// <summary>Describes one persisted recovery graph whose exact native install set has a live owner.</summary>
internal sealed class LeasedProtectedRecoveryGraphSelection
{
    internal LeasedProtectedRecoveryGraphSelection(
        ProtectedGraphSnapshot graph,
        IReadOnlyList<ResolvedPackage> packages,
        PackageGraphUseLeaseOwner leaseOwner)
    {
        Graph = (graph ?? throw new ArgumentNullException(nameof(graph))).Copy();
        Packages = Array.AsReadOnly((packages ?? throw new ArgumentNullException(nameof(packages))).ToArray());
        LeaseOwner = leaseOwner ?? throw new ArgumentNullException(nameof(leaseOwner));
    }

    internal ProtectedGraphSnapshot Graph { get; }
    internal IReadOnlyList<ResolvedPackage> Packages { get; }
    internal PackageGraphUseLeaseOwner LeaseOwner { get; }
}

/// <summary>Contains the exact selected recovery graphs and owners for participant-mode Loading.</summary>
internal sealed class LeasedPackageGraphRecoveryLoadingHandoff
{
    internal LeasedPackageGraphRecoveryLoadingHandoff(
        string correlationId,
        IReadOnlyDictionary<string, string> currentActiveVersions,
        IReadOnlyList<LeasedProtectedRecoveryGraphSelection> graphSelections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(currentActiveVersions);
        ArgumentNullException.ThrowIfNull(graphSelections);
        CorrelationId = correlationId;
        CurrentActiveVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            currentActiveVersions.ToDictionary(static item => item.Key, static item => item.Value,
                StringComparer.OrdinalIgnoreCase));
        GraphSelections = Array.AsReadOnly(graphSelections.ToArray());
    }

    internal string CorrelationId { get; }
    internal IReadOnlyDictionary<string, string> CurrentActiveVersions { get; }
    internal IReadOnlyList<LeasedProtectedRecoveryGraphSelection> GraphSelections { get; }
}
