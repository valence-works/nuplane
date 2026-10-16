using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Versioning;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Checks persisted graph selections without performing filesystem I/O or granting authority.</summary>
/// <remarks>
/// The result contains descriptive graph candidates only. Callers that admit an enrolled member
/// must separately validate every install against held native handles. This verifier binds the
/// state body, protection record, active package maps, descriptors, activation records, and both
/// graph closures into one coherent persisted selection.
/// </remarks>
internal static class PersistedStoreStateGraphVerifier
{
    private static readonly NuGetVersionRangeEvaluator VersionRanges = new();

    /// <summary>Verifies one self-contained persisted state and its active/recoverable graph sets.</summary>
    /// <param name="state">The decoded store state.</param>
    /// <returns>Immutable descriptive active and UseLastKnownGood graph candidates.</returns>
    /// <exception cref="PackageStoreAdmissionException">The state is malformed or inconsistent.</exception>
    internal static VerifiedStoreStateGraphs Verify(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            return VerifyCore(state);
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          NullReferenceException or OverflowException or KeyNotFoundException)
        {
            throw Refused("The persisted store state contains malformed graph or protection data.",
                state.ProtectionRecord?.RootIdentity, exception);
        }
    }

    /// <summary>
    /// Verifies the complete persisted UseLastKnownGood selection for the current startup policy.
    /// </summary>
    /// <remarks>
    /// The current policy requires each active package ID/version to appear in the LKG map,
    /// matching existing startup recovery behavior; unused LKG entries are ignored. The selected
    /// graph structure comes only from versioned persisted snapshots and is never reconstructed
    /// from package/version maps.
    /// </remarks>
    /// <param name="state">The decoded store state.</param>
    /// <returns>The verified descriptive graph sets; use <see cref="VerifiedStoreStateGraphs.RecoverableGraphs"/> for the selected fallback.</returns>
    /// <exception cref="PackageStoreAdmissionException">The current policy cannot select a complete graph set.</exception>
    internal static VerifiedStoreStateGraphs SelectUseLastKnownGood(StoreStateRecord state)
    {
        var verified = Verify(state);
        if (state.ActiveVersionById.Count == 0)
            throw Refused("UseLastKnownGood cannot recover a state with no active packages.", state.ProtectionRecord?.RootIdentity);
        return verified;
    }

    /// <summary>
    /// Verifies one acknowledged member under an exact root, epoch, and operation status.
    /// </summary>
    /// <remarks>
    /// For Incomplete enrollment finalization this accepts only a fully acknowledged, non-pending
    /// bound union whose members equal its target set. It grants no admission and performs no native
    /// identity checks. The caller must invoke it for every locked member and separately revalidate
    /// each state slot and each install identity.
    /// </remarks>
    /// <param name="state">The decoded member state.</param>
    /// <param name="ledger">The reread root-membership record.</param>
    /// <param name="member">The member record from that ledger.</param>
    /// <param name="expectedRoot">The independently held physical root identity.</param>
    /// <param name="expectedEpoch">The expected membership epoch.</param>
    /// <param name="requiredStatus">The operation-specific required ledger status.</param>
    /// <returns>Immutable descriptive active and recoverable graph candidates.</returns>
    /// <exception cref="PackageStoreAdmissionException">The state or membership acknowledgement does not match.</exception>
    internal static VerifiedStoreStateGraphs VerifyAcknowledgedMember(
        StoreStateRecord state,
        RootMembershipRecord ledger,
        RootMemberRecord member,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        RootMembershipStatus requiredStatus)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(expectedRoot);

        try
        {
            if (ledger.SchemaVersion != RootMembershipRecord.CurrentSchemaVersion ||
                ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEpoch ||
                expectedEpoch <= 0 || ledger.Status != requiredStatus || !Enum.IsDefined(requiredStatus) ||
                ledger.PendingStateCommit is not null)
            {
                throw Refused("The root membership does not match the required root, epoch, status, and non-pending state.", expectedRoot);
            }

            if (!string.Equals(ProtectionDigest.Ledger(ledger), ledger.LedgerDigest, StringComparison.Ordinal))
                throw Refused("The root membership digest is invalid.", expectedRoot);

            var membersById = new Dictionary<string, RootMemberRecord>(StringComparer.Ordinal);
            foreach (var item in ledger.Members)
            {
                if (item is null || !membersById.TryAdd(item.MemberId, item))
                    throw Refused("The root membership contains a duplicate or null member.", expectedRoot);
            }

            var targetIds = ledger.TargetMemberIds.ToHashSet(StringComparer.Ordinal);
            if (membersById.Count == 0 || targetIds.Count != ledger.TargetMemberIds.Count ||
                !membersById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(targetIds))
            {
                throw Refused("The bound member union must equal the exact target member set.", expectedRoot);
            }

            foreach (var target in membersById.Values)
            {
                if (target.Binding is not RootMemberRecord.AcknowledgedBinding acknowledged ||
                    acknowledged.ProtectionRecord.RootIdentity != expectedRoot ||
                    acknowledged.ProtectionRecord.EnrollmentEpoch != expectedEpoch ||
                    !string.Equals(acknowledged.ProtectionRecord.MemberId, target.MemberId, StringComparison.Ordinal))
                {
                    throw Refused("Every target member must have an acknowledgement for the expected root and epoch.", expectedRoot);
                }

                ValidateProtectionCandidate(acknowledged.ProtectionRecord, expectedRoot);
                if (acknowledged.ProtectionRecord.LegacyUnknownRecovery ||
                    acknowledged.ProtectionRecord.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    acknowledged.ProtectionRecord.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    !string.Equals(ProtectionDigest.Protection(acknowledged.ProtectionRecord),
                        acknowledged.ProtectionRecord.ProtectionDigest, StringComparison.Ordinal))
                {
                    throw Refused("Every acknowledged target must carry a digest-valid known active and recoverable closure.", expectedRoot);
                }
            }

            if (!membersById.TryGetValue(member.MemberId, out var ledgerMember) ||
                !SameMember(ledgerMember, member) ||
                ledgerMember.Binding is not RootMemberRecord.AcknowledgedBinding binding)
            {
                throw Refused("The supplied member is not the exact acknowledged member in the reread ledger.", expectedRoot);
            }

            var verified = Verify(state);
            var protection = state.ProtectionRecord!;
            if (protection.RootIdentity != expectedRoot || protection.EnrollmentEpoch != expectedEpoch ||
                !string.Equals(protection.MemberId, member.MemberId, StringComparison.Ordinal) ||
                protection.Revision != binding.ProtectionRecord.Revision ||
                !state.ProtectionRecord!.HasSamePayloadAs(binding.ProtectionRecord))
            {
                throw Refused("The member state does not exactly match its acknowledged root, epoch, revision, and protection payload.", expectedRoot);
            }

            return verified;
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          NullReferenceException or OverflowException or KeyNotFoundException)
        {
            throw Refused("The member acknowledgement or ledger is malformed.", expectedRoot, exception);
        }
    }

    private static VerifiedStoreStateGraphs VerifyCore(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state.ActiveVersionById);
        ArgumentNullException.ThrowIfNull(state.LastKnownGoodById);
        ArgumentNullException.ThrowIfNull(state.LastFailureById);
        ArgumentNullException.ThrowIfNull(state.LastSuccessfulSourceSnapshots);

        var protection = state.ProtectionRecord ??
            throw Refused("The state has no persisted package-protection record.");
        ValidateProtectionCandidate(protection, protection.RootIdentity);

        if (!string.Equals(ProtectionDigest.StateBody(state), protection.StateBodyDigest, StringComparison.Ordinal))
            throw Refused("The protection record does not bind the complete state body.", protection.RootIdentity);
        if (!string.Equals(ProtectionDigest.Protection(protection), protection.ProtectionDigest, StringComparison.Ordinal))
            throw Refused("The package-protection digest is invalid.", protection.RootIdentity);

        var activeVersions = CopyVersionMap(state.ActiveVersionById, "active version map");
        var lastKnownGoodVersions = CopyVersionMap(state.LastKnownGoodById, "last-known-good version map");
        if (!ActiveVersionsMatchLastKnownGood(activeVersions, lastKnownGoodVersions))
            throw Refused("Every active package/version must be present in the last-known-good map.", protection.RootIdentity);

        if (protection.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            protection.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            protection.LegacyUnknownRecovery)
        {
            throw Refused("Both graph closures must be Known and legacy recovery must be resolved.", protection.RootIdentity);
        }

        var activeGraphs = protection.ActiveClosure.Graphs!;
        var recoverableGraphs = protection.RecoverableClosure.Graphs!;
        var activeKeys = new HashSet<GraphKey>();
        var recoverableByKey = new Dictionary<GraphKey, ProtectedGraphSnapshot>();
        foreach (var graph in activeGraphs)
        {
            ValidateGraphSnapshot(graph, ProtectedGraphDisposition.Active, protection, activeVersions);
            if (!activeKeys.Add(KeyFor(graph)))
                throw Refused("The active closure contains duplicate graph identities.", protection.RootIdentity);
        }

        foreach (var graph in recoverableGraphs)
        {
            ValidateGraphSnapshot(graph, ProtectedGraphDisposition.Recoverable, protection, lastKnownGoodVersions);
            if (!recoverableByKey.TryAdd(KeyFor(graph), graph))
                throw Refused("The recoverable closure contains duplicate graph identities.", protection.RootIdentity);
        }

        ValidateRecoverableSelection(activeGraphs, recoverableByKey, protection);
        ValidateActiveGraphRecords(state, activeGraphs, protection);
        ValidateActiveDescriptors(state, activeGraphs, protection);

        return new VerifiedStoreStateGraphs(activeGraphs, recoverableGraphs);
    }

    private static void ValidateProtectionCandidate(PackageProtectionRecord protection, PhysicalRootIdentity expectedRoot)
    {
        if (protection.SchemaVersion != PackageProtectionRecord.CurrentSchemaVersion ||
            protection.RootIdentity != expectedRoot || protection.EnrollmentEpoch <= 0 || protection.Revision <= 0 ||
            string.IsNullOrWhiteSpace(protection.MemberId))
        {
            throw Refused("The protection record has an unsupported schema or invalid root/member/revision binding.", expectedRoot);
        }

        ProtectionDigest.ValidateCanonicalDigest(protection.StateBodyDigest);
        ProtectionDigest.ValidateCanonicalDigest(protection.ProtectionDigest);
        foreach (var retired in protection.RetiredGraphs)
        {
            if (retired.RetiringEpoch > protection.EnrollmentEpoch || retired.RetiringRevision > protection.Revision)
                throw Refused("Retired graph evidence is from a future root epoch or state revision.", expectedRoot);
            ProtectionDigest.ValidateCanonicalDigest(retired.ProofDigest);
        }
    }

    private static Dictionary<string, string> CopyVersionMap(IReadOnlyDictionary<string, string> versions, string description)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, version) in versions)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version) ||
                !NuGet.Versioning.NuGetVersion.TryParse(version, out _) || !normalized.TryAdd(packageId, version))
            {
                throw Refused($"The {description} contains a blank, duplicate, or invalid package/version entry.");
            }
        }
        return normalized;
    }

    private static bool ActiveVersionsMatchLastKnownGood(
        IReadOnlyDictionary<string, string> active,
        IReadOnlyDictionary<string, string> lastKnownGood)
    {
        foreach (var (packageId, version) in active)
        {
            if (!lastKnownGood.TryGetValue(packageId, out var lkgVersion) ||
                !string.Equals(version, lkgVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateGraphSnapshot(
        ProtectedGraphSnapshot graph,
        ProtectedGraphDisposition requiredDisposition,
        PackageProtectionRecord protection,
        IReadOnlyDictionary<string, string> selectedVersions)
    {
        if (graph is null || (graph.Disposition & requiredDisposition) == 0 ||
            string.IsNullOrWhiteSpace(graph.GraphId) || string.IsNullOrWhiteSpace(graph.GenerationId) ||
            graph.Nodes.Count == 0 || graph.RequestedRoots.Count == 0)
        {
            throw Refused("A protected graph snapshot is incomplete or has the wrong disposition.", protection.RootIdentity);
        }

        if (requiredDisposition == ProtectedGraphDisposition.Recoverable)
        {
            var evidence = graph.RecoverySelectionEvidence;
            if (evidence is null || !string.Equals(evidence.RecoveryPolicyId,
                    RecoverableGraphSnapshotFactory.RecoveryPolicyId, StringComparison.Ordinal) ||
                evidence.SourceRevision <= 0 || evidence.SourceRevision > protection.Revision)
            {
                throw Refused("A recoverable graph is not bound to the supported versioned startup policy and source revision.", protection.RootIdentity);
            }

            if (!HaveSameItems(graph.RequestedRoots.Select(static root => root.SelectedNodeId),
                    evidence.SelectedRootNodeIds))
            {
                throw Refused("Recovery-selection evidence does not name every persisted requested root selection.", protection.RootIdentity);
            }
        }

        var nodesById = new Dictionary<Guid, PackageInstallIdentity>();
        var nodesByPackage = new Dictionary<string, PackageInstallIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.NodeId == Guid.Empty || node.Install is null ||
                !nodesById.TryAdd(node.NodeId, node.Install) ||
                !nodesByPackage.TryAdd(node.Install.PackageId, node.Install) ||
                node.Install.Root != protection.RootIdentity ||
                !selectedVersions.TryGetValue(node.Install.PackageId, out var expectedVersion) ||
                !string.Equals(expectedVersion, node.Install.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw Refused("A protected graph node is duplicate or does not match the selected package root/version map.", protection.RootIdentity);
            }
        }

        var selectedRootIds = new HashSet<Guid>();
        foreach (var selection in graph.RequestedRoots)
        {
            if (selection is null || selection.Request is null ||
                string.IsNullOrWhiteSpace(selection.Request.Id) || selection.Request.VersionRange is null ||
                string.IsNullOrWhiteSpace(selection.Request.SourceName) ||
                (selection.Request.FeedName is not null && string.IsNullOrWhiteSpace(selection.Request.FeedName)) ||
                !Enum.IsDefined(selection.Request.UpdatePolicy) ||
                !nodesById.TryGetValue(selection.SelectedNodeId, out var selected) ||
                !string.Equals(selection.Request.Id, selected.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !VersionRanges.SelectBestMatch(selection.Request.VersionRange, [selected.Version]).Success)
            {
                throw Refused("A protected graph request does not select its recorded package/version node.", protection.RootIdentity);
            }
            selectedRootIds.Add(selection.SelectedNodeId);
        }

        if (selectedRootIds.Count == 0)
            throw Refused("A protected graph has no selected root nodes.", protection.RootIdentity);

        var adjacency = nodesById.Keys.ToDictionary(static nodeId => nodeId, static _ => new List<Guid>());
        foreach (var edge in graph.Edges)
        {
            if (edge is null || !nodesById.ContainsKey(edge.FromNodeId) ||
                !nodesById.TryGetValue(edge.ToNodeId, out var target) ||
                !string.Equals(edge.RequestedPackageId, target.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !VersionRanges.SelectBestMatch(edge.RequestedVersionRange, [target.Version]).Success)
            {
                throw Refused("A protected graph edge has a missing endpoint or an unsatisfied requested package/version.", protection.RootIdentity);
            }

            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var reachable = new HashSet<Guid>();
        var pending = new Stack<Guid>(selectedRootIds);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current))
                continue;
            foreach (var target in adjacency[current])
                pending.Push(target);
        }
        if (reachable.Count != nodesById.Count)
            throw Refused("A protected graph contains nodes unreachable from its selected roots.", protection.RootIdentity);
    }

    private static void ValidateRecoverableSelection(
        IReadOnlyList<ProtectedGraphSnapshot> activeGraphs,
        IReadOnlyDictionary<GraphKey, ProtectedGraphSnapshot> recoverableByKey,
        PackageProtectionRecord protection)
    {
        if (activeGraphs.Count != recoverableByKey.Count)
            throw Refused("The recoverable closure is not the exact graph set selected by current UseLastKnownGood semantics.", protection.RootIdentity);

        foreach (var active in activeGraphs)
        {
            if ((active.Disposition & ProtectedGraphDisposition.Recoverable) == 0 ||
                !recoverableByKey.TryGetValue(KeyFor(active), out var recoverable) ||
                !active.HasSamePayloadAs(recoverable))
            {
                throw Refused("UseLastKnownGood must select the exact persisted active graph snapshots, without historical map-only reconstruction.", protection.RootIdentity);
            }
        }
    }

    private static void ValidateActiveGraphRecords(
        StoreStateRecord state,
        IReadOnlyList<ProtectedGraphSnapshot> activeGraphs,
        PackageProtectionRecord protection)
    {
        var snapshotsByKey = new Dictionary<GraphKey, ProtectedGraphSnapshot>();
        foreach (var graph in activeGraphs)
        {
            if (!snapshotsByKey.TryAdd(KeyFor(graph), graph))
                throw Refused("The active graph closure contains duplicate graph/generation identities.", protection.RootIdentity);
        }

        var activationByKey = new Dictionary<GraphKey, GraphActivationRecord>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.ActiveGraphsByIdNormalized)
        {
            var record = pair.Value;
            if (record is null || string.IsNullOrWhiteSpace(pair.Key) ||
                !string.Equals(pair.Key, record.GraphId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.GraphId) || string.IsNullOrWhiteSpace(record.GenerationId) ||
                record.Status != GraphActivationStatus.Active || record.Failure is not null ||
                !seenKeys.Add(record.GraphId))
            {
                throw Refused("The active graph record map contains an invalid or non-active graph entry.", protection.RootIdentity);
            }

            var key = new GraphKey(record.GraphId, record.GenerationId);
            if (!activationByKey.TryAdd(key, record) || !snapshotsByKey.TryGetValue(key, out var snapshot))
                throw Refused("An active graph record has no exact protected graph snapshot.", protection.RootIdentity);

            var nodeByPackage = snapshot.Nodes.ToDictionary(static node => node.Install.PackageId,
                static node => node.Install, StringComparer.OrdinalIgnoreCase);
            var rootIds = snapshot.RequestedRoots
                .Select(selection => nodeByPackage[selection.Request.Id].PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!UniqueSetEquals(record.NodePackageIds, nodeByPackage.Keys) ||
                !UniqueSetEquals(record.RootPackageIds, rootIds) ||
                record.NodeVersionsByPackageId is null ||
                record.NodeVersionsByPackageId.Count != nodeByPackage.Count ||
                !DictionaryVersionsEqual(record.NodeVersionsByPackageId, nodeByPackage))
            {
                throw Refused("An active graph record's roots, nodes, or selected versions differ from its protected snapshot.", protection.RootIdentity);
            }
        }

        if (activationByKey.Count != snapshotsByKey.Count)
            throw Refused("Active graph records and protected active snapshots are not an exact graph set.", protection.RootIdentity);
    }

    private static bool DictionaryVersionsEqual(
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyDictionary<string, PackageInstallIdentity> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, version) in versions)
        {
            if (!seen.Add(packageId) || !nodes.TryGetValue(packageId, out var install) ||
                !string.Equals(version, install.Version, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return seen.SetEquals(nodes.Keys);
    }

    private static void ValidateActiveDescriptors(
        StoreStateRecord state,
        IReadOnlyList<ProtectedGraphSnapshot> activeGraphs,
        PackageProtectionRecord protection)
    {
        var projections = new Dictionary<string, DescriptorProjection>(StringComparer.OrdinalIgnoreCase);
        foreach (var graph in activeGraphs)
        {
            var installsByNodeId = graph.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
            var rootNodeIds = graph.RequestedRoots.Select(static request => request.SelectedNodeId).ToHashSet();
            var rootPackageIds = rootNodeIds.Select(nodeId => installsByNodeId[nodeId].PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var dependencyOfByNodeId = new Dictionary<Guid, HashSet<string>>();
            foreach (var edge in graph.Edges)
            {
                if (!dependencyOfByNodeId.TryGetValue(edge.ToNodeId, out var dependencies))
                {
                    dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dependencyOfByNodeId.Add(edge.ToNodeId, dependencies);
                }
                dependencies.Add(installsByNodeId[edge.FromNodeId].PackageId);
            }

            foreach (var node in graph.Nodes)
            {
                var isRoot = rootNodeIds.Contains(node.NodeId);
                var isDependency = dependencyOfByNodeId.TryGetValue(node.NodeId, out var dependencies) && dependencies.Count > 0;
                var role = (isRoot, isDependency) switch
                {
                    (true, true) => ActivePackageRole.RootAndDependency,
                    (true, false) => ActivePackageRole.Root,
                    (false, true) => ActivePackageRole.Dependency,
                    _ => throw Refused("A protected active graph contains a node with no root or dependency role.", protection.RootIdentity)
                };
                var projection = new DescriptorProjection(node.Install, graph.GraphId, graph.GenerationId, role,
                    rootPackageIds, dependencyOfByNodeId.GetValueOrDefault(node.NodeId) ?? []);
                if (projections.TryGetValue(node.Install.PackageId, out var current))
                    current.Merge(projection, protection.RootIdentity);
                else
                    projections.Add(node.Install.PackageId, projection);
            }
        }

        var descriptors = state.ActivePackageDescriptorsByIdNormalized;
        if (descriptors.Count != projections.Count)
            throw Refused("The active descriptor map does not exactly cover the protected active package set.", protection.RootIdentity);

        foreach (var (key, descriptor) in descriptors)
        {
            if (descriptor is null || !projections.TryGetValue(key, out var expected) ||
                !string.Equals(key, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.PackageId, expected.Install.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.Version, expected.Install.Version, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(descriptor.InstallPath) ||
                descriptor.PackageRole != expected.Role ||
                descriptor.Discoverable != (expected.Role != ActivePackageRole.Dependency) ||
                !UniqueSetEquals(descriptor.RootPackageIds, expected.RootPackageIds) ||
                !UniqueSetEquals(descriptor.DependencyOfPackageIds, expected.DependencyOfPackageIds) ||
                !expected.Graphs.Contains(new GraphKey(descriptor.GraphId, descriptor.GraphGenerationId)))
            {
                throw Refused("An active descriptor does not match the exact package, graph, role, and dependency projection.", protection.RootIdentity);
            }
        }

        foreach (var packageId in state.ActiveVersionById.Keys)
        {
            if (!projections.ContainsKey(packageId))
                throw Refused("The active version map contains a package with no protected graph node.", protection.RootIdentity);
        }
    }

    private static bool UniqueSetEquals(IEnumerable<string>? values, IEnumerable<string> expected)
    {
        if (values is null)
            return false;
        var items = values.ToArray();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items.All(value => !string.IsNullOrWhiteSpace(value) && unique.Add(value)) &&
               unique.SetEquals(expected);
    }

    private static bool HaveSameItems<T>(IEnumerable<T> left, IEnumerable<T> right) where T : notnull
    {
        var counts = new Dictionary<T, int>();
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

    private static GraphKey KeyFor(ProtectedGraphSnapshot graph) => new(graph.GraphId, graph.GenerationId);

    private static bool SameMember(RootMemberRecord left, RootMemberRecord right)
    {
        if (!string.Equals(left.MemberId, right.MemberId, StringComparison.Ordinal) ||
            !string.Equals(left.ConfiguredLocator, right.ConfiguredLocator, StringComparison.Ordinal) ||
            left.Binding is not RootMemberRecord.AcknowledgedBinding leftBinding ||
            right.Binding is not RootMemberRecord.AcknowledgedBinding rightBinding)
        {
            return false;
        }

        return leftBinding.StateSlot == rightBinding.StateSlot &&
               leftBinding.ObservedStateFileIdentity == rightBinding.ObservedStateFileIdentity &&
               leftBinding.ProtectionRecord.HasSamePayloadAs(rightBinding.ProtectionRecord);
    }

    private static PackageStoreAdmissionException Refused(
        string message,
        PhysicalRootIdentity? root = null,
        Exception? inner = null)
        => new(PackageStoreAdmissionReason.StateMismatch, message, root, inner);

    private readonly record struct GraphKey(string GraphId, string GenerationId);

    private sealed class DescriptorProjection(
        PackageInstallIdentity install,
        string graphId,
        string generationId,
        ActivePackageRole role,
        IEnumerable<string> rootPackageIds,
        IEnumerable<string> dependencyOfPackageIds)
    {
        internal PackageInstallIdentity Install { get; } = install;
        internal ActivePackageRole Role { get; private set; } = role;
        internal HashSet<string> RootPackageIds { get; } = new(rootPackageIds, StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> DependencyOfPackageIds { get; } = new(dependencyOfPackageIds, StringComparer.OrdinalIgnoreCase);
        internal HashSet<GraphKey> Graphs { get; } = [new GraphKey(graphId, generationId)];

        internal void Merge(DescriptorProjection other, PhysicalRootIdentity root)
        {
            if (Install != other.Install)
                throw Refused("One active package ID refers to different selected install identities across graphs.", root);
            if (Role != other.Role)
                Role = ActivePackageRole.RootAndDependency;
            RootPackageIds.UnionWith(other.RootPackageIds);
            DependencyOfPackageIds.UnionWith(other.DependencyOfPackageIds);
            Graphs.UnionWith(other.Graphs);
        }
    }

    /// <summary>Verified immutable graph candidates with no native handle or admission authority.</summary>
    internal sealed class VerifiedStoreStateGraphs
    {
        internal VerifiedStoreStateGraphs(
            IEnumerable<ProtectedGraphSnapshot> activeGraphs,
            IEnumerable<ProtectedGraphSnapshot> recoverableGraphs)
        {
            ActiveGraphs = new ReadOnlyCollection<ProtectedGraphSnapshot>(activeGraphs.ToArray());
            RecoverableGraphs = new ReadOnlyCollection<ProtectedGraphSnapshot>(recoverableGraphs.ToArray());
        }

        /// <summary>Gets the exact active graph snapshots described by the state.</summary>
        internal IReadOnlyList<ProtectedGraphSnapshot> ActiveGraphs { get; }

        /// <summary>Gets the snapshots selected by the versioned current UseLastKnownGood policy.</summary>
        internal IReadOnlyList<ProtectedGraphSnapshot> RecoverableGraphs { get; }
    }
}
