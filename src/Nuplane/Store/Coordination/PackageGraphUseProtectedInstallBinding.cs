using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

/// <summary>Joins one persisted recovery graph to its exact current native install paths.</summary>
/// <remarks>
/// The persisted snapshot and computed paths are descriptive only. This binding retains native
/// member-locator and install observations while Core publishes graph-use records, and grants no
/// package payload access by itself.
/// </remarks>
internal sealed class PackageGraphUseProtectedInstallBinding : IDisposable
{
    private readonly RootMembershipRegistry.MemberLocatorReplayScope _scope;
    private readonly IReadOnlyList<PackageGraphUseInstallBinding.NativeInstallPathObservation> _observations;
    private bool _disposed;

    private PackageGraphUseProtectedInstallBinding(
        RootMembershipRegistry.MemberLocatorReplayScope scope,
        IReadOnlyList<PackageGraphUseInstallBinding.NativeInstallPathObservation> observations,
        IReadOnlyDictionary<Guid, string> exactPathsByNode)
    {
        _scope = scope;
        _observations = observations;
        ExactPathsByNode = exactPathsByNode;
    }

    internal IReadOnlyDictionary<Guid, string> ExactPathsByNode { get; }

    internal static PackageGraphUseProtectedInstallBinding Observe(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle heldRoot,
        RootMembershipRecord lockedLedger,
        ProtectedGraphSnapshot graph,
        string configuredInstallRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(lockedLedger);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredInstallRoot);
        if ((graph.Disposition & ProtectedGraphDisposition.Recoverable) == 0 ||
            graph.RecoverySelectionEvidence is null)
            throw Refused("Recovery graph binding requires the exact persisted recoverable candidate.", lockedLedger.RootIdentity);

        var exactPathsByNode = graph.Nodes.ToDictionary(
            static node => node.NodeId,
            node => GetExactCandidatePath(configuredInstallRoot, node.Install));
        return ObserveExactPaths(files, registry, heldRoot, lockedLedger, graph, exactPathsByNode, "recovery");
    }

    internal static PackageGraphUseProtectedInstallBinding ObserveActive(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle heldRoot,
        RootMembershipRecord lockedLedger,
        ProtectedGraphSnapshot graph,
        IReadOnlyDictionary<Guid, string> exactPathsByNode)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(lockedLedger);
        ArgumentNullException.ThrowIfNull(exactPathsByNode);
        if ((graph.Disposition & ProtectedGraphDisposition.Active) == 0)
            throw Refused("Active graph binding requires an exact persisted active candidate.", lockedLedger.RootIdentity);
        return ObserveExactPaths(files, registry, heldRoot, lockedLedger, graph, exactPathsByNode, "active");
    }

    private static PackageGraphUseProtectedInstallBinding ObserveExactPaths(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle heldRoot,
        RootMembershipRecord lockedLedger,
        ProtectedGraphSnapshot graph,
        IReadOnlyDictionary<Guid, string> exactPathsByNode,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(lockedLedger);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(exactPathsByNode);
        if (lockedLedger.Status != RootMembershipStatus.Complete || lockedLedger.PendingStateCommit is not null)
            throw Refused($"{operation} graph binding requires Complete, non-pending locked membership.", lockedLedger.RootIdentity);
        if (graph.Roots.Count != 1 || graph.Roots[0] != lockedLedger.RootIdentity ||
            graph.Nodes.Any(node => node.Install.Root != lockedLedger.RootIdentity))
        {
            throw Refused($"A participant {operation} graph must belong exactly to the admitted physical root.", lockedLedger.RootIdentity);
        }
        if (exactPathsByNode.Count != graph.Nodes.Count ||
            graph.Nodes.Any(node => !exactPathsByNode.ContainsKey(node.NodeId)))
        {
            throw Refused($"{operation} graph binding requires one exact install path per protected node.", lockedLedger.RootIdentity);
        }

        var scope = new RootMembershipRegistry.MemberLocatorReplayScope(lockedLedger);
        var observations = new List<PackageGraphUseInstallBinding.NativeInstallPathObservation>(graph.Nodes.Count);
        try
        {
            var rootHandleInfo = files.InspectHandle(heldRoot);
            if (rootHandleInfo.Kind != PhysicalStoreEntryKind.Directory ||
                rootHandleInfo.Identity != lockedLedger.RootIdentity.HandleIdentity)
            {
                throw Refused($"The held {operation} root differs from the acknowledged membership root.", lockedLedger.RootIdentity);
            }

            var resolver = new PackageStoreAuthorityResolver(files, registry);
            var reader = new PackageInstallIdentityReader(files);
            var paths = new Dictionary<Guid, string>();
            var exactNames = new HashSet<string>(StringComparer.Ordinal);
            var directoryIdentities = new HashSet<PhysicalFileIdentity>();
            foreach (var node in graph.Nodes)
            {
                var expected = node.Install;
                var candidatePath = exactPathsByNode[node.NodeId];
                ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
                if (!exactNames.Add(candidatePath))
                    throw Refused($"Two persisted {operation} nodes share one exact install path.", lockedLedger.RootIdentity);

                var observed = PackageGraphUseInstallBinding.ObserveNativeInstallPath(
                    files, resolver, reader, scope, heldRoot, lockedLedger.RootIdentity,
                    candidatePath, expected.RootRelativeInstallPath, expected.PackageId, expected.Version,
                    expected.VerifiedArchiveHash);
                observations.Add(observed);
                if (observed.InstallIdentity != expected || !directoryIdentities.Add(expected.DirectoryIdentity))
                    throw Refused($"A persisted {operation} install identity differs from its current native completion observation.", lockedLedger.RootIdentity);
                if (!paths.TryAdd(node.NodeId, candidatePath))
                    throw Refused($"A persisted {operation} graph contains a duplicate node identity.", lockedLedger.RootIdentity);
            }

            var binding = new PackageGraphUseProtectedInstallBinding(scope, observations,
                new ReadOnlyDictionary<Guid, string>(paths));
            binding.Revalidate();
            return binding;
        }
        catch (Exception primary)
        {
            var cleanupFailures = new List<Exception>();
            for (var index = observations.Count - 1; index >= 0; index--)
            {
                try { observations[index].Dispose(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            scope.Expire();
            if (cleanupFailures.Count > 0)
                throw new AggregateException($"{operation} install binding failed and native observations did not close cleanly.",
                    new[] { primary }.Concat(cleanupFailures));
            throw;
        }
    }

    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scope.EnsureActive();
        foreach (var observation in _observations)
            observation.Revalidate();
    }

    internal static string GetExactCandidatePath(string configuredInstallRoot, PackageInstallIdentity install)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredInstallRoot);
        ArgumentNullException.ThrowIfNull(install);
        if (!Path.IsPathFullyQualified(configuredInstallRoot))
            throw Refused("The selected trusted install root is not fully qualified.", install.Root);

        var components = PackageInstallIdentityReader.ValidateRelativeInstallPath(
            install.RootRelativeInstallPath, install.PackageId, install.Version);
        return Path.GetFullPath(components.Aggregate(configuredInstallRoot,
            static (parent, component) => Path.Combine(parent, component)));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var failures = new List<Exception>();
        for (var index = _observations.Count - 1; index >= 0; index--)
        {
            try { _observations[index].Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        _scope.Expire();
        if (failures.Count > 0)
            throw new AggregateException("Native graph-use install observations did not close cleanly.", failures);
    }

    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity root)
        => new(PackageStoreAdmissionReason.StateMismatch, message, root);
}
