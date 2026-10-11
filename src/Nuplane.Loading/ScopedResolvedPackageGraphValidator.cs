using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

internal static class ScopedResolvedPackageGraphValidator
{
    internal static void Validate(ScopedResolvedPackageGraph scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        var graph = scoped.Graph;
        var lease = scoped.LeaseOwner.Lease;
        var snapshot = lease.Snapshot;
        if (!string.Equals(graph.GraphId, snapshot.GraphId, StringComparison.Ordinal) ||
            !string.Equals(graph.GenerationId, snapshot.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(graph.GraphId, Reconciliation.Models.ResolvedPackageGraph.CreateGraphId(
                graph.TargetFramework, graph.Roots, graph.Nodes, graph.Edges, graph.SourceDecisions), StringComparison.Ordinal))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The supplied resolved graph does not match its published graph-use lease.", lease);
        }

        var graphNodes = UniqueByKey(graph.Nodes, static node => Key(node.PackageId, node.Version), "resolved graph node", lease);
        var snapshotNodes = UniqueByKey(snapshot.Nodes, static node => Key(node.Install.PackageId, node.Install.Version), "leased graph node", lease);
        if (graphNodes.Count == 0 || graphNodes.Count != snapshotNodes.Count ||
            !graphNodes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshotNodes.Keys))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The resolved graph node set differs from the immutable leased graph.", lease);
        }

        foreach (var (key, node) in graphNodes)
        {
            var install = snapshotNodes[key].Install;
            if (!string.Equals(node.PackageId, install.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(node.Version, install.Version, StringComparison.Ordinal) ||
                (node.PackageContentHash is not null &&
                 !string.Equals(node.PackageContentHash, install.VerifiedArchiveHash, StringComparison.Ordinal)))
            {
                throw Refused(PackageStoreAdmissionReason.StateMismatch,
                    "A resolved graph node identity differs from its published install identity.", lease);
            }
        }

        var packages = UniqueByKey(scoped.Packages, static package => Key(package.Id, package.Version), "package projection", lease);
        if (packages.Count != graphNodes.Count || !packages.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(graphNodes.Keys))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The package projection does not exactly cover the resolved graph.", lease);
        }

        foreach (var (key, node) in graphNodes)
        {
            var package = packages[key];
            var expectedSourceName = string.IsNullOrWhiteSpace(package.SourceName) ? package.FeedName : package.SourceName;
            if (!string.Equals(package.Id, node.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(package.Version, node.Version, StringComparison.Ordinal) ||
                !string.Equals(package.InstallPath, node.InstallPath, StringComparison.Ordinal) ||
                !string.Equals(package.PackageContentHash, node.PackageContentHash, StringComparison.Ordinal) ||
                !string.Equals(expectedSourceName, node.SourceName, StringComparison.OrdinalIgnoreCase))
            {
                throw Refused(PackageStoreAdmissionReason.StateMismatch,
                    "A package projection does not match the resolved node's identity, source, hash, or exact original path.", lease);
            }

            using var pin = lease.AcquireRead(package.InstallPath);
            var install = lease.GetInstallIdentityForExactPath(package.InstallPath);
            if (!string.Equals(install.PackageId, node.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(install.Version, node.Version, StringComparison.Ordinal) ||
                install != snapshotNodes[key].Install)
            {
                throw Refused(PackageStoreAdmissionReason.RootMismatch,
                    "The exact package path is bound to a different leased package identity.", lease);
            }
        }

        var roots = graph.Roots.Select(static root => Key(root.PackageId, root.Version)).ToHashSet(StringComparer.Ordinal);
        if (roots.Count != graph.Roots.Count || roots.Count == 0 ||
            graph.Roots.Any(root => !graphNodes.TryGetValue(Key(root.PackageId, root.Version), out var node) ||
                root.Role != node.Role || !string.Equals(root.InstallPath, node.InstallPath, StringComparison.Ordinal)))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "Resolved graph roots do not match their selected nodes.", lease);
        }

        if (scoped.OriginalRootRequests.Count != snapshot.RequestedRoots.Count ||
            !scoped.OriginalRootRequests.SequenceEqual(snapshot.RequestedRoots.Select(static root => root.Request)))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The original root requests differ from those published in the graph-use lease.", lease);
        }

        var nodeKeyById = snapshot.Nodes.ToDictionary(static node => node.NodeId,
            static node => Key(node.Install.PackageId, node.Install.Version));
        var selectedRoots = snapshot.RequestedRoots.Select(root => nodeKeyById[root.SelectedNodeId]).ToHashSet(StringComparer.Ordinal);
        if (!selectedRoots.SetEquals(roots))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The leased original requests do not select exactly the resolved graph roots.", lease);
        }

        var resolvedEdges = graph.Edges.Select(edge => new EdgeKey(
            Key(edge.FromPackageId, edge.FromVersion),
            Key(edge.ToPackageId, edge.SelectedVersion),
            edge.ToPackageId,
            edge.RequestedVersionRange,
            edge.DependencyGroupTargetFramework,
            edge.Optional));
        var leasedEdges = snapshot.Edges.Select(edge => new EdgeKey(
            nodeKeyById[edge.FromNodeId],
            nodeKeyById[edge.ToNodeId],
            edge.RequestedPackageId,
            edge.RequestedVersionRange,
            edge.TargetFramework,
            edge.IsOptional));
        if (!SameMultiset(resolvedEdges, leasedEdges))
        {
            throw Refused(PackageStoreAdmissionReason.StateMismatch,
                "The resolved dependency edges differ from the immutable leased graph.", lease);
        }
    }

    private static Dictionary<string, T> UniqueByKey<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string description,
        PackageGraphUseLease lease)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!result.TryAdd(keySelector(value), value))
                throw Refused(PackageStoreAdmissionReason.StateMismatch,
                    $"The {description} collection contains a duplicate package/version identity.", lease);
        }
        return result;
    }

    private static bool SameMultiset(IEnumerable<EdgeKey> left, IEnumerable<EdgeKey> right)
    {
        var counts = new Dictionary<EdgeKey, int>();
        foreach (var item in left)
            counts[item] = counts.GetValueOrDefault(item) + 1;
        foreach (var item in right)
        {
            if (!counts.TryGetValue(item, out var count))
                return false;
            if (count == 1)
                counts.Remove(item);
            else
                counts[item] = count - 1;
        }
        return counts.Count == 0;
    }

    private static string Key(string packageId, string version)
        => packageId.ToUpperInvariant() + "\0" + version.ToUpperInvariant();

    private static PackageStoreAdmissionException Refused(
        PackageStoreAdmissionReason reason,
        string message,
        PackageGraphUseLease lease)
        => new(reason, message, lease.Snapshot.Roots.Count == 1 ? lease.Snapshot.Roots[0] : null);

    private sealed record EdgeKey(
        string From,
        string To,
        string RequestedPackageId,
        string RequestedVersionRange,
        string TargetFramework,
        bool Optional);
}
