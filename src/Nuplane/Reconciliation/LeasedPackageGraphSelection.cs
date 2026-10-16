using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;

/// <summary>Associates one successfully applied complete graph with its final roots and lease owner.</summary>
public sealed class LeasedPackageGraphSelection
{
    internal LeasedPackageGraphSelection(ResolvedPackageGraphSelection selection, PackageGraphUseLeaseOwner owner)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Graph = selection.Graph;
        RootRequests = Array.AsReadOnly(selection.RootRequests.Select(static request => request with { }).ToArray());
        Packages = Array.AsReadOnly(selection.Packages.ToArray());
        LeaseOwner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>Gets the actual complete resolved graph selected during this reconciliation cycle.</summary>
    public ResolvedPackageGraph Graph { get; }

    /// <summary>Gets the exact final requests that selected this graph's roots.</summary>
    public IReadOnlyList<PackageRequest> RootRequests { get; }

    /// <summary>Gets the packages that passed this graph's complete activation transaction.</summary>
    public IReadOnlyList<ResolvedPackage> Packages { get; }

    /// <summary>Gets the published owner that must be disposed or transferred to a retaining lifetime.</summary>
    public PackageGraphUseLeaseOwner LeaseOwner { get; }
}
