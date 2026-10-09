using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.ProtectionRecords;

internal static class ProtectionRecordValueCopies
{
    internal static bool HaveSameItems<T>(IEnumerable<T> first, IEnumerable<T> second) where T : notnull
    {
        var counts = new Dictionary<T, int>();
        foreach (var value in first)
            counts[value] = counts.GetValueOrDefault(value) + 1;
        foreach (var value in second)
        {
            if (!counts.TryGetValue(value, out var count))
                return false;
            if (count == 1)
                counts.Remove(value);
            else
                counts[value] = count - 1;
        }
        return counts.Count == 0;
    }

    internal static PhysicalRootIdentity CopyRoot(PhysicalRootIdentity root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(root.HandleIdentity);
        return new PhysicalRootIdentity(CopyIdentity(root.HandleIdentity));
    }

    internal static PhysicalFileIdentity CopyIdentity(PhysicalFileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new PhysicalFileIdentity(identity.Provider, identity.VolumeOrDeviceId, identity.FileId);
    }

    internal static PackageGraphRootSelection CopyRootSelection(PackageGraphRootSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Request);
        var request = selection.Request;
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceName);
        if (request.FeedName is not null && string.IsNullOrWhiteSpace(request.FeedName))
            throw new ArgumentException("A requested root's feed name cannot be blank when specified.", nameof(selection));
        if (!Enum.IsDefined(request.UpdatePolicy))
            throw new ArgumentOutOfRangeException(nameof(selection), "A requested root has an unsupported update policy.");

        return new PackageGraphRootSelection(
            new PackageRequest(request.Id, request.VersionRange, request.FeedName, request.UpdatePolicy, request.SourceName),
            selection.SelectedNodeId);
    }

    internal static PackageGraphNodeIdentity CopyNode(PackageGraphNodeIdentity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(node.Install);
        var install = node.Install;
        var copiedInstall = new PackageInstallIdentity(
            CopyRoot(install.Root),
            install.PackageId,
            install.Version,
            install.RootRelativeInstallPath,
            CopyIdentity(install.DirectoryIdentity),
            install.CompletionIdentity,
            install.VerifiedArchiveHash);
        return new PackageGraphNodeIdentity(node.NodeId, copiedInstall);
    }

    internal static PackageGraphEdgeIdentity CopyEdge(PackageGraphEdgeIdentity edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new PackageGraphEdgeIdentity(
            edge.FromNodeId,
            edge.ToNodeId,
            edge.RequestedPackageId,
            edge.RequestedVersionRange,
            edge.TargetFramework,
            edge.IsOptional);
    }
}
