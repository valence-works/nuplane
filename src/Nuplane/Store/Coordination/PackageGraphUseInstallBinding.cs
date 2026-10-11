using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

/// <summary>Retains native install observations for one root's exact graph paths.</summary>
/// <remarks>
/// Only call within the admitted root callback, while all member locks remain held. The binding
/// owns its observations, never the borrowed root, and must be disposed before that callback ends.
/// It grants no read capability or graph-completeness claim. Archive hashes remain caller-provided
/// descriptive values; no package payload is opened here.
/// </remarks>
internal sealed class PackageGraphUseInstallBinding : IDisposable
{
    private readonly RootMembershipRegistry.MemberLocatorReplayScope _scope;
    private readonly IReadOnlyList<IDisposable> _observations;
    private readonly IReadOnlyList<Action> _revalidations;
    private bool _disposed;

    private PackageGraphUseInstallBinding(
        RootMembershipRegistry.MemberLocatorReplayScope scope,
        IReadOnlyList<IDisposable> observations,
        IReadOnlyList<Action> revalidations,
        IReadOnlyList<BoundInstall> installs)
    {
        _scope = scope;
        _observations = observations;
        _revalidations = revalidations;
        Installs = new ReadOnlyCollection<BoundInstall>(installs.ToArray());
    }

    internal IReadOnlyList<BoundInstall> Installs { get; }

    /// <summary>Joins still-held native observations to the exact selected graph nodes and paths.</summary>
    /// <remarks>The returned candidate is descriptive and does not outlive the caller's native verification authority.</remarks>
    internal static BoundGraphCandidate CreateActiveCandidate(
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> requests,
        IReadOnlyList<PackageGraphUseInstallBinding> rootBindings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(rootBindings);
        var boundByNode = new Dictionary<ResolvedPackageNode, BoundInstall>(ReferenceEqualityComparer.Instance);
        foreach (var binding in rootBindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            binding.Revalidate();
            foreach (var install in binding.Installs)
            {
                if (!boundByNode.TryAdd(install.Node, install))
                    throw Refused("A graph node has more than one native install binding.");
            }
        }

        var nodes = graph.Nodes.ToArray();
        if (nodes.Length != boundByNode.Count || nodes.Any(node =>
                !boundByNode.TryGetValue(node, out var binding) ||
                !string.Equals(node.InstallPath, binding.ExactInstallPath, StringComparison.Ordinal)))
            throw Refused("Native install bindings must match the exact selected graph nodes and original paths.");

        var candidate = RecoverableGraphSnapshotFactory.CreateActiveCandidate(graph, requests,
            nodes.Select(node => boundByNode[node].Identity).ToArray());
        var paths = candidate.Nodes.Select((node, index) => (node.NodeId, boundByNode[nodes[index]].ExactInstallPath))
            .ToDictionary(static item => item.NodeId, static item => item.ExactInstallPath);
        return new BoundGraphCandidate(candidate, new ReadOnlyDictionary<Guid, string>(paths));
    }

    internal static PackageGraphUseInstallBinding Observe(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle heldRoot,
        RootMembershipRecord lockedLedger,
        IReadOnlyList<ResolvedPackageNode> rootNodes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(lockedLedger);
        ArgumentNullException.ThrowIfNull(rootNodes);
        if (lockedLedger.Status != RootMembershipStatus.Complete || lockedLedger.PendingStateCommit is not null)
            throw Refused("Graph-use binding requires Complete, non-pending locked membership.");
        var rootInfo = files.InspectHandle(heldRoot);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != lockedLedger.RootIdentity.HandleIdentity)
            throw Refused("The graph-use binding root differs from the locked membership root.");

        var scope = new RootMembershipRegistry.MemberLocatorReplayScope(lockedLedger);
        var observations = new List<IDisposable>();
        var revalidations = new List<Action>();
        try
        {
            var nodes = rootNodes.ToArray();
            if (nodes.Length == 0 || nodes.Any(static node => node is null))
                throw new ArgumentException("A root binding requires a non-empty selected node set.", nameof(rootNodes));
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<PhysicalFileIdentity>();
            var installs = new List<BoundInstall>(nodes.Length);
            var resolver = new PackageStoreAuthorityResolver(files, registry);
            var reader = new PackageInstallIdentityReader(files);
            foreach (var node in nodes)
            {
                var path = node.InstallPath;
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                if (!paths.Add(path))
                    throw Refused("Distinct graph nodes cannot share one exact install path.");

                var resolved = resolver.ResolveProtectedInstallPath(path, scope);
                observations.Add(resolved);
                var target = files.InspectHandle(resolved.Target);
                if (resolved.RootIdentity != lockedLedger.RootIdentity || target.Kind != PhysicalStoreEntryKind.Directory)
                    throw Refused("A selected graph path does not identify a directory in the locked root.");

                // The suffix is only a candidate. Both independent native walks must identify the
                // same completed directory; string prefix/normalization never establishes authority.
                var hint = GetInstallSuffixHint(path);
                var observed = reader.Observe(heldRoot, lockedLedger.RootIdentity, hint,
                    node.PackageId, node.Version, node.PackageContentHash);
                observations.Add(observed);
                if (observed.InstallIdentity.DirectoryIdentity != target.Identity || !identities.Add(target.Identity))
                    throw Refused("A graph path aliases a different install or another selected node's native directory.");

                revalidations.Add(resolved.Revalidate);
                revalidations.Add(observed.Revalidate);
                installs.Add(new BoundInstall(node, path, observed.InstallIdentity));
            }

            var binding = new PackageGraphUseInstallBinding(scope, observations, revalidations, installs);
            binding.Revalidate();
            return binding;
        }
        catch (Exception primary)
        {
            var failures = DisposeObservations(observations);
            scope.Expire();
            if (failures.Count != 0)
                throw new AggregateException("Graph-use binding failed and native observations did not close cleanly.",
                    new[] { primary }.Concat(failures));
            throw;
        }
    }

    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scope.EnsureActive();
        foreach (var revalidate in _revalidations)
            revalidate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var failures = DisposeObservations(_observations);
        _scope.Expire();
        if (failures.Count != 0)
            throw new AggregateException("Native graph-use observations did not close cleanly.", failures);
    }

    private static string GetInstallSuffixHint(string exactPath)
    {
        if (!Path.IsPathFullyQualified(exactPath))
            throw Refused("A selected graph install path must be fully qualified.");
        var components = exactPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (components.Length < 3)
            throw Refused("A selected graph path has no feed/package/version suffix.");
        return string.Join('/', components[^3..]);
    }

    private static List<Exception> DisposeObservations(IReadOnlyList<IDisposable> observations)
    {
        var failures = new List<Exception>();
        for (var index = observations.Count - 1; index >= 0; index--)
        {
            try { observations[index].Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        return failures;
    }

    private static PackageStoreAdmissionException Refused(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    internal sealed record BoundInstall(ResolvedPackageNode Node, string ExactInstallPath, PackageInstallIdentity Identity);

    internal sealed record BoundGraphCandidate(ProtectedGraphSnapshot Candidate, IReadOnlyDictionary<Guid, string> ExactPathsByNode);
}
