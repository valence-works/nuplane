using Nuplane.Abstractions;

namespace Nuplane.Reconciliation;

/// <summary>Contains exact applied graph selections and owners for the deferred Loading phase.</summary>
public sealed class LeasedPackageGraphLoadingHandoff
{
    internal LeasedPackageGraphLoadingHandoff(
        PackageChangeSet changeSet,
        IReadOnlyList<PackageRequest> originalDesiredRequests,
        IReadOnlyList<LeasedPackageGraphSelection> graphSelections)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ChangeSet = changeSet with
        {
            Added = Array.AsReadOnly(changeSet.Added.ToArray()),
            Updated = Array.AsReadOnly(changeSet.Updated.ToArray()),
            Removed = Array.AsReadOnly(changeSet.Removed.ToArray())
        };
        OriginalDesiredRequests = Array.AsReadOnly((originalDesiredRequests
            ?? throw new ArgumentNullException(nameof(originalDesiredRequests))).Select(static request => request with { }).ToArray());
        GraphSelections = Array.AsReadOnly((graphSelections
            ?? throw new ArgumentNullException(nameof(graphSelections))).ToArray());
    }

    /// <summary>Gets the completed cycle change set.</summary>
    public PackageChangeSet ChangeSet { get; }

    /// <summary>Gets the original desired requests read for this cycle, before contributor expansion.</summary>
    public IReadOnlyList<PackageRequest> OriginalDesiredRequests { get; }

    /// <summary>Gets each successful complete graph with its exact final root requests and lease owner.</summary>
    public IReadOnlyList<LeasedPackageGraphSelection> GraphSelections { get; }
}
