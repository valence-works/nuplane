using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Loading;

/// <summary>Joins one persisted active graph and its projections to its exact committed native lease.</summary>
internal static class ScopedProtectedActiveGraphValidator
{
    internal static void Validate(LeasedProtectedActiveGraphSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var graph = selection.Graph;
        var owner = selection.LeaseOwner;
        var snapshot = owner.Lease.Snapshot;
        if ((graph.Disposition & ProtectedGraphDisposition.Active) == 0 ||
            snapshot.State != PackageGraphUseSnapshotState.Committed ||
            !string.Equals(graph.GraphId, snapshot.GraphId, StringComparison.Ordinal) ||
            !string.Equals(graph.GenerationId, snapshot.GenerationId, StringComparison.Ordinal) ||
            graph.SnapshotId != snapshot.SnapshotId ||
            !graph.Roots.SequenceEqual(snapshot.Roots) ||
            !graph.RequestedRoots.SequenceEqual(snapshot.RequestedRoots) ||
            !graph.Nodes.SequenceEqual(snapshot.Nodes) ||
            !graph.Edges.SequenceEqual(snapshot.Edges))
        {
            throw Refused("The persisted active graph does not match its exact committed graph-use publication.", snapshot);
        }

        if (graph.Roots.Count != 1 || graph.Nodes.Count == 0 ||
            graph.Nodes.Any(node => node.Install.Root != graph.Roots[0]) ||
            selection.Packages.Count != graph.Nodes.Count)
        {
            throw Refused("A participant active graph must be complete and belong to exactly one physical root.", snapshot);
        }

        var nodesByPackage = graph.Nodes.ToDictionary(static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in selection.Packages)
        {
            if (package is null || !seen.Add(package.Id) ||
                !nodesByPackage.TryGetValue(package.Id, out var node) ||
                !string.Equals(package.Version, node.Install.Version, StringComparison.Ordinal) ||
                !string.Equals(package.PackageContentHash, node.Install.VerifiedArchiveHash, StringComparison.Ordinal) ||
                owner.Lease.GetInstallIdentityForExactPath(package.InstallPath) != node.Install)
            {
                throw Refused("A package projection differs from the persisted active graph or its exact native lease.", snapshot);
            }
        }
    }

    private static PackageStoreAdmissionException Refused(string message, PackageGraphUseSnapshot snapshot) =>
        new(PackageStoreAdmissionReason.StateMismatch, message,
            snapshot.Roots.Count == 1 ? snapshot.Roots[0] : null);
}
