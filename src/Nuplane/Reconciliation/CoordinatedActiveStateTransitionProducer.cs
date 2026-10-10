using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>Builds a complete protected active-state candidate under an existing admitted owner.</summary>
/// <remarks>
/// This type performs no publication and grants no operation authority. It observes selected installs
/// while the caller's Complete root and member locks are held, then returns a descriptive complete
/// candidate. The caller must publish it through <see cref="ICoordinatedStoreRegistry.PersistCoordinatedActiveStateAsync"/>
/// using the same live borrow; that writer independently repeats complete native and semantic verification.
/// </remarks>
internal static class CoordinatedActiveStateTransitionProducer
{
    /// <summary>Builds the next fully verified state for one configured member and existing owner.</summary>
    internal static async Task<StoreStateRecord> CreateCandidateAsync(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry membershipRegistry,
        ICoordinatedStoreRegistry stateRegistry,
        PackageStoreOperationBorrow borrow,
        IReadOnlyDictionary<string, string> mergedActiveVersions,
        IReadOnlyList<ResolvedPackage> successfullyAppliedPackages,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackageGraphSelection> graphSelections,
        IReadOnlySet<string> desiredRootPackageIds,
        IReadOnlySet<string> failedPackageIds,
        string correlationId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(membershipRegistry);
        ArgumentNullException.ThrowIfNull(stateRegistry);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(mergedActiveVersions);
        ArgumentNullException.ThrowIfNull(successfullyAppliedPackages);
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(graphSelections);
        ArgumentNullException.ThrowIfNull(desiredRootPackageIds);
        ArgumentNullException.ThrowIfNull(failedPackageIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReferenceEquals(files, membershipRegistry.Files))
            throw Refused("Active-state construction must use the root membership's exact native provider.", borrow.Root);

        var nextActive = CopyMap(mergedActiveVersions, "merged active package map", borrow.Root);
        var appliedPackages = successfullyAppliedPackages.ToArray();
        var appliedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in appliedPackages)
        {
            if (package is null || string.IsNullOrWhiteSpace(package.Id) || string.IsNullOrWhiteSpace(package.Version) ||
                !appliedVersions.TryAdd(package.Id, package.Version) ||
                !nextActive.TryGetValue(package.Id, out var selectedVersion) ||
                !VersionEquals(selectedVersion, package.Version))
            {
                throw Refused("Successfully applied packages must uniquely match the merged active selection.", borrow.Root);
            }
        }

        var desiredRoots = CopySet(desiredRootPackageIds, "desired root set", borrow.Root);
        var failedPackages = CopySet(failedPackageIds, "failed package set", borrow.Root);
        var selections = graphSelections.ToArray();
        changeSet = changeSet with
        {
            Added = changeSet.Added.ToArray(),
            Updated = changeSet.Updated.ToArray(),
            Removed = changeSet.Removed.ToArray()
        };
        ValidateSelections(selections, desiredRoots, borrow.Root);

        // This exact configured slot read uses the owner already retained by the caller. The callback
        // below replays the complete member union before and after native graph binding.
        var current = await stateRegistry.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
        var priorProtection = current.ProtectionRecord ??
            throw Refused("A protected active transition requires a current acknowledged protection record.", borrow.Root);
        var verifiedPrior = PersistedStoreStateGraphVerifier.Verify(current);

        return await PackageStoreOperationAccess.WithValidatedRootAsync(borrow,
            (heldFiles, heldRoot, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(heldFiles, files))
                    throw Refused("The admitted root callback changed native filesystem providers.", borrow.Root);

                var locked = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                var ledger = locked.Ledger;
                if (ledger.Status != RootMembershipStatus.Complete || ledger.PendingStateCommit is not null ||
                    ledger.RootIdentity != borrow.Root || ledger.EnrollmentEpoch != borrow.Epoch ||
                    priorProtection.RootIdentity != borrow.Root || priorProtection.EnrollmentEpoch != borrow.Epoch)
                {
                    throw Refused("The selected state and held membership do not share the borrowed Complete root, epoch, and member.", borrow.Root);
                }

                var acknowledged = ledger.Members.SingleOrDefault(member =>
                    string.Equals(member.MemberId, priorProtection.MemberId, StringComparison.Ordinal));
                if (acknowledged?.Binding is not RootMemberRecord.AcknowledgedBinding binding ||
                    !binding.ProtectionRecord.HasSamePayloadAs(priorProtection))
                {
                    throw Refused("The configured state is not the exact current acknowledged member payload.", borrow.Root);
                }

                var activated = new List<IncomingSelection>();
                foreach (var selection in selections)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsFullyActive(selection.Graph, nextActive))
                        continue;

                    using var installBinding = PackageGraphUseInstallBinding.Observe(
                        files, membershipRegistry, heldRoot, ledger, selection.Graph.Nodes);
                    var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                        selection.Graph,
                        selection.RootRequests,
                        installBinding.Installs.Select(static install => install.Identity).ToArray(),
                        checked(priorProtection.Revision + 1));
                    installBinding.Revalidate();
                    var exactPaths = installBinding.Installs.ToDictionary(
                        static install => new PackageVersionKey(install.Node.PackageId, install.Node.Version),
                        static install => install.ExactInstallPath,
                        PackageVersionKeyComparer.Instance);
                    activated.Add(new IncomingSelection(selection, snapshot, exactPaths));
                }

                var activatedRoots = activated.SelectMany(static item => item.Selection.RootRequests)
                    .Select(static request => request.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (desiredRoots.Any(id => !activatedRoots.Contains(id) && !failedPackages.Contains(id)))
                    throw Refused("Every desired root requires a fully active selected graph or explicit failure evidence.", borrow.Root);

                ValidateAppliedPackages(appliedPackages, activated, borrow.Root);

                var incomingGraphs = activated.Select(static item => item.Selection.Graph).ToArray();
                var incomingInstalls = GetUniqueIncomingInstalls(activated, borrow.Root);
                var retention = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
                    current,
                    nextActive,
                    desiredRoots,
                    failedPackages,
                    incomingGraphs,
                    incomingInstalls);
                if (retention.PriorProtectionRevision != priorProtection.Revision ||
                    !string.Equals(retention.PriorProtectionDigest, priorProtection.ProtectionDigest, StringComparison.Ordinal) ||
                    !string.Equals(retention.PriorStateBodyDigest, priorProtection.StateBodyDigest, StringComparison.Ordinal))
                {
                    throw Refused("Historical retention was prepared from a different prior state revision.", borrow.Root);
                }

                var mappedGraphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                    current, incomingGraphs, nextActive, correlationId, activatedAtUtc, desiredRoots, failedPackages);
                var stateDescriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
                    current, nextActive, appliedPackages, changeSet, correlationId, activatedAtUtc,
                    incomingGraphs, mappedGraphRecords);

                var snapshots = SelectFinalSnapshots(
                    verifiedPrior.ActiveGraphs,
                    activated,
                    retention.RetainedSnapshots,
                    mappedGraphRecords,
                    nextActive,
                    desiredRoots,
                    failedPackages,
                    borrow.Root);
                var records = BuildExactActivationRecords(
                    snapshots,
                    mappedGraphRecords,
                    verifiedPrior.ActiveGraphs,
                    current.ActiveGraphsByIdNormalized,
                    activatedAtUtc,
                    correlationId,
                    borrow.Root);
                var descriptors = BuildExactDescriptors(
                    current,
                    stateDescriptors,
                    snapshots,
                    activated,
                    nextActive,
                    borrow.Root);

                var lastKnownGood = new Dictionary<string, string>(current.LastKnownGoodById, StringComparer.OrdinalIgnoreCase);
                foreach (var (packageId, version) in appliedVersions)
                    lastKnownGood[packageId] = version;

                var next = new StoreStateRecord(
                    new Dictionary<string, string>(nextActive, StringComparer.OrdinalIgnoreCase),
                    lastKnownGood,
                    new Dictionary<string, FailureRecord>(current.LastFailureById, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, SourceSnapshotRef>(current.LastSuccessfulSourceSnapshots, StringComparer.OrdinalIgnoreCase),
                    activatedAtUtc,
                    new Dictionary<string, ActivePackageDescriptor>(descriptors, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, GraphActivationRecord>(records, StringComparer.OrdinalIgnoreCase));

                var nextRevision = checked(priorProtection.Revision + 1);
                var activeClosure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, snapshots);
                var recoverableClosure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, snapshots);
                var retired = CreateRetirementEvidence(current, priorProtection, next, activeClosure,
                    recoverableClosure, nextRevision, snapshots, borrow.Root);
                var protection = CreateProtection(priorProtection, next, activeClosure, recoverableClosure,
                    retired, nextRevision);
                next = next with { ProtectionRecord = protection };

                _ = PersistedStoreStateGraphVerifier.Verify(next);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(next);
            }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ProtectedGraphSnapshot> SelectFinalSnapshots(
        IReadOnlyList<ProtectedGraphSnapshot> priorSnapshots,
        IReadOnlyList<IncomingSelection> incoming,
        IReadOnlyList<ProtectedGraphSnapshot> retained,
        IReadOnlyDictionary<string, GraphActivationRecord> mappedRecords,
        IReadOnlyDictionary<string, string> nextActive,
        IReadOnlySet<string> desiredRoots,
        IReadOnlySet<string> failedPackages,
        PhysicalRootIdentity root)
    {
        var result = new Dictionary<GraphKey, ProtectedGraphSnapshot>();
        foreach (var selection in incoming)
        {
            var prior = priorSnapshots.SingleOrDefault(snapshot => KeyFor(snapshot) == KeyFor(selection.Snapshot));
            var snapshot = prior is not null && EquivalentSelection(prior, selection.Snapshot)
                ? prior
                : selection.Snapshot;
            AddSnapshot(result, snapshot, root);
        }

        foreach (var snapshot in retained)
            AddSnapshot(result, snapshot, root);

        var failedDesired = desiredRoots.Where(failedPackages.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in priorSnapshots)
        {
            var graphKey = KeyFor(snapshot);
            if (result.ContainsKey(graphKey) || !mappedRecords.TryGetValue(snapshot.GraphId, out var record) ||
                !GraphKeyMatches(record, graphKey) || !IsFullyActive(snapshot, nextActive))
            {
                continue;
            }

            var rootIds = GetRootPackageIds(snapshot);
            var selectedFailedRoots = rootIds.Where(failedDesired.Contains).ToArray();
            // When only a subset of a prior multi-root graph failed, retention intentionally
            // replaces the parent with the exact failed-root forward subclosure.
            if (selectedFailedRoots.Length != 0 && selectedFailedRoots.Length != rootIds.Count)
                continue;
            AddSnapshot(result, snapshot, root);
        }

        return result.Values.OrderBy(static graph => graph.GraphId, StringComparer.Ordinal)
            .ThenBy(static graph => graph.GenerationId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, GraphActivationRecord> BuildExactActivationRecords(
        IReadOnlyList<ProtectedGraphSnapshot> snapshots,
        IReadOnlyDictionary<string, GraphActivationRecord> mappedRecords,
        IReadOnlyList<ProtectedGraphSnapshot> priorSnapshots,
        IReadOnlyDictionary<string, GraphActivationRecord> priorRecords,
        DateTimeOffset activatedAtUtc,
        string correlationId,
        PhysicalRootIdentity root)
    {
        var records = new Dictionary<string, GraphActivationRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            GraphActivationRecord record;
            if (mappedRecords.TryGetValue(snapshot.GraphId, out var mapped) && GraphKeyMatches(mapped, KeyFor(snapshot)))
            {
                record = mapped;
            }
            else if (priorRecords.TryGetValue(snapshot.GraphId, out var prior) && GraphKeyMatches(prior, KeyFor(snapshot)) &&
                     ActivationRecordMatchesSnapshot(prior, snapshot))
            {
                record = prior;
            }
            else
            {
                var parent = FindHistoricalParent(snapshot, priorSnapshots, priorRecords, root);
                record = CreateSubclosureActivationRecord(snapshot, parent);
            }

            if (!ActivationRecordMatchesSnapshot(record, snapshot) || !records.TryAdd(snapshot.GraphId, record))
                throw Refused("Active graph activation records must exactly cover the final protected snapshots.", root);
        }
        return records;
    }

    private static GraphActivationRecord FindHistoricalParent(
        ProtectedGraphSnapshot retained,
        IReadOnlyList<ProtectedGraphSnapshot> priorSnapshots,
        IReadOnlyDictionary<string, GraphActivationRecord> priorRecords,
        PhysicalRootIdentity root)
    {
        var candidates = priorSnapshots.Where(parent => parent.GenerationId == retained.GenerationId &&
                IsExactSubclosure(parent, retained))
            .Where(parent => priorRecords.TryGetValue(parent.GraphId, out var record) &&
                GraphKeyMatches(record, KeyFor(parent)) && ActivationRecordMatchesSnapshot(record, parent))
            .Select(parent => priorRecords[parent.GraphId])
            .ToArray();
        if (candidates.Length != 1)
            throw Refused("A retained failed-root subclosure must have one exact prior activation lineage.", root);
        return candidates[0];
    }

    private static bool IsExactSubclosure(ProtectedGraphSnapshot parent, ProtectedGraphSnapshot child)
    {
        var parentNodes = parent.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
        if (child.Nodes.Any(node => !parentNodes.TryGetValue(node.NodeId, out var install) || install != node.Install))
            return false;
        var parentEdges = parent.Edges.ToHashSet();
        if (child.Edges.Any(edge => !parentEdges.Contains(edge)))
            return false;
        var parentRoots = parent.RequestedRoots.ToHashSet();
        return child.RequestedRoots.All(parentRoots.Contains);
    }

    private static GraphActivationRecord CreateSubclosureActivationRecord(
        ProtectedGraphSnapshot snapshot,
        GraphActivationRecord parent) =>
        new(
            snapshot.GraphId,
            snapshot.GenerationId,
            GetRootPackageIds(snapshot).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            snapshot.Nodes.Select(static node => node.Install.PackageId).ToArray(),
            parent.ActivatedAtUtc,
            parent.CorrelationId,
            GraphActivationStatus.Active,
            NodeVersionsByPackageId: snapshot.Nodes.ToDictionary(static node => node.Install.PackageId,
                static node => node.Install.Version, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, ActivePackageDescriptor> BuildExactDescriptors(
        StoreStateRecord current,
        IReadOnlyDictionary<string, ActivePackageDescriptor> mappedDescriptors,
        IReadOnlyList<ProtectedGraphSnapshot> snapshots,
        IReadOnlyList<IncomingSelection> incoming,
        IReadOnlyDictionary<string, string> nextActive,
        PhysicalRootIdentity root)
    {
        var incomingByIdentity = new Dictionary<PackageVersionKey, PackageLineage>(PackageVersionKeyComparer.Instance);
        foreach (var selection in incoming)
        {
            var packageByIdentity = selection.Selection.Packages.ToDictionary(
                static package => new PackageVersionKey(package.Id, package.Version),
                static package => package,
                PackageVersionKeyComparer.Instance);
            foreach (var node in selection.Selection.Graph.Nodes)
            {
                var key = new PackageVersionKey(node.PackageId, node.Version);
                var package = packageByIdentity[key];
                var path = selection.ExactPaths[key];
                var lineage = new PackageLineage(package.FeedName, package.SourceName, path, package.PackageContentHash);
                if (incomingByIdentity.TryGetValue(key, out var previous) && previous != lineage)
                    throw Refused("Selected graphs disagree on exact install path or package source lineage.", root);
                incomingByIdentity[key] = lineage;
            }
        }

        var projections = new Dictionary<string, DescriptorProjection>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            var installsByNode = snapshot.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
            var rootNodeIds = snapshot.RequestedRoots.Select(static request => request.SelectedNodeId).ToHashSet();
            var roots = rootNodeIds.Select(nodeId => installsByNode[nodeId].PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var parentsByNode = new Dictionary<Guid, HashSet<string>>();
            foreach (var edge in snapshot.Edges)
            {
                if (!parentsByNode.TryGetValue(edge.ToNodeId, out var parents))
                {
                    parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    parentsByNode.Add(edge.ToNodeId, parents);
                }
                parents.Add(installsByNode[edge.FromNodeId].PackageId);
            }

            foreach (var node in snapshot.Nodes)
            {
                var install = node.Install;
                if (!nextActive.TryGetValue(install.PackageId, out var activeVersion) || !VersionEquals(activeVersion, install.Version))
                    throw Refused("A final active snapshot does not match the merged active version map.", root);
                var isRoot = rootNodeIds.Contains(node.NodeId);
                var parentIds = parentsByNode.GetValueOrDefault(node.NodeId) ?? [];
                var isDependency = parentIds.Count > 0;
                var role = (isRoot, isDependency) switch
                {
                    (true, true) => ActivePackageRole.RootAndDependency,
                    (true, false) => ActivePackageRole.Root,
                    (false, true) => ActivePackageRole.Dependency,
                    _ => throw Refused("A final active graph contains an unprojectable node role.", root)
                };

                ActivePackageDescriptor priorDescriptor = current.ActivePackageDescriptorsByIdNormalized
                    .GetValueOrDefault(install.PackageId)!;
                PackageLineage lineage;
                if (incomingByIdentity.TryGetValue(new PackageVersionKey(install.PackageId, install.Version), out var incomingLineage))
                {
                    lineage = incomingLineage;
                    if (priorDescriptor is not null &&
                        string.Equals(priorDescriptor.Version, install.Version, StringComparison.OrdinalIgnoreCase) &&
                        IsHistoricalSnapshot(snapshot, incoming, install) &&
                        (!string.Equals(priorDescriptor.FeedName, incomingLineage.FeedName, StringComparison.Ordinal) ||
                         !string.Equals(priorDescriptor.SourceName, incomingLineage.SourceName, StringComparison.Ordinal) ||
                         !string.Equals(priorDescriptor.InstallPath, incomingLineage.InstallPath, StringComparison.Ordinal)))
                    {
                        throw Refused("An incoming graph conflicts with retained package source lineage or exact install path.", root);
                    }
                }
                else
                {
                    if (priorDescriptor is null || !string.Equals(priorDescriptor.Version, install.Version, StringComparison.OrdinalIgnoreCase))
                        throw Refused("A carried or retained install has no exact prior descriptor lineage.", root);
                    lineage = new PackageLineage(priorDescriptor.FeedName, priorDescriptor.SourceName,
                        priorDescriptor.InstallPath, install.VerifiedArchiveHash);
                }

                if (!string.Equals(lineage.InstallPath, GetExactPath(snapshot, install, incoming, current), StringComparison.Ordinal))
                    throw Refused("Active graph projections disagree on the exact persisted install path.", root);

                var graphKey = KeyFor(snapshot);
                var projection = new DescriptorProjection(install, lineage, graphKey, role, roots, parentIds);
                if (projections.TryGetValue(install.PackageId, out var existing))
                    existing.Merge(projection, root);
                else
                    projections.Add(install.PackageId, projection);
            }
        }

        if (projections.Count != nextActive.Count || nextActive.Keys.Any(packageId => !projections.ContainsKey(packageId)))
            throw Refused("The final graph snapshots do not exactly cover every merged active package.", root);

        var result = new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, projection) in projections)
        {
            if (!mappedDescriptors.TryGetValue(packageId, out var descriptor) || descriptor is null ||
                !string.Equals(descriptor.Version, projection.Install.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw Refused("An active graph package has no matching host-facing descriptor metadata.", root);
            }
            var chosen = projection.Graphs.Contains(new GraphKey(descriptor.GraphId, descriptor.GraphGenerationId))
                ? new GraphKey(descriptor.GraphId, descriptor.GraphGenerationId)
                : projection.Graphs.OrderBy(static key => key.GraphId, StringComparer.Ordinal)
                    .ThenBy(static key => key.GenerationId, StringComparer.Ordinal).First();
            result.Add(packageId, descriptor with
            {
                FeedName = projection.Lineage.FeedName,
                SourceName = projection.Lineage.SourceName,
                InstallPath = projection.Lineage.InstallPath,
                GraphId = chosen.GraphId,
                GraphGenerationId = chosen.GenerationId,
                PackageRole = projection.Role,
                RootPackageIds = projection.Roots.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                DependencyOfPackageIds = projection.Dependencies.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                Discoverable = projection.Role is not ActivePackageRole.Dependency
            });
        }
        return result;
    }

    private static bool IsHistoricalSnapshot(
        ProtectedGraphSnapshot snapshot,
        IReadOnlyList<IncomingSelection> incoming,
        PackageInstallIdentity install)
        => !incoming.Any(selection => KeyFor(selection.Snapshot) == KeyFor(snapshot)) &&
           snapshot.Nodes.Any(node => node.Install == install);

    private static string GetExactPath(
        ProtectedGraphSnapshot snapshot,
        PackageInstallIdentity install,
        IReadOnlyList<IncomingSelection> incoming,
        StoreStateRecord current)
    {
        foreach (var selection in incoming)
        {
            if (KeyFor(selection.Snapshot) == KeyFor(snapshot))
            {
                var key = new PackageVersionKey(install.PackageId, install.Version);
                if (selection.ExactPaths.TryGetValue(key, out var path))
                    return path;
            }
        }
        if (current.ActivePackageDescriptorsByIdNormalized.TryGetValue(install.PackageId, out var descriptor) &&
            string.Equals(descriptor.Version, install.Version, StringComparison.OrdinalIgnoreCase))
            return descriptor.InstallPath;
        throw Refused("A protected graph node has no exact previously verified install path.", install.Root);
    }

    private static IReadOnlyList<RetiredGraphEvidence> CreateRetirementEvidence(
        StoreStateRecord current,
        PackageProtectionRecord prior,
        StoreStateRecord next,
        PackageProtectionClosure active,
        PackageProtectionClosure recoverable,
        long nextRevision,
        IReadOnlyList<ProtectedGraphSnapshot> nextSnapshots,
        PhysicalRootIdentity root)
    {
        var retainedIds = nextSnapshots.Select(static snapshot => snapshot.SnapshotId).ToHashSet();
        var retired = prior.RetiredGraphs.ToList();
        var newlyUnselected = prior.ActiveClosure.Graphs!.Where(snapshot => !retainedIds.Contains(snapshot.SnapshotId)).ToArray();
        if (newlyUnselected.Length == 0)
            return retired;

        var stateDigest = ProtectionDigest.StateBody(next);
        var preview = new PackageProtectionRecord(
            prior.SchemaVersion, prior.RootIdentity, prior.EnrollmentEpoch, prior.MemberId, nextRevision,
            stateDigest, new string('0', 64), active, recoverable, prior.RetiredGraphs, false);
        var selectionDigest = ProtectionDigest.Protection(preview);
        foreach (var snapshot in newlyUnselected)
        {
            if (retired.Any(item => item.SnapshotId == snapshot.SnapshotId))
                throw Refused("A graph snapshot cannot be retired twice.", root);
            retired.Add(new RetiredGraphEvidence(
                snapshot.SnapshotId,
                snapshot.GraphId,
                snapshot.GenerationId,
                prior.EnrollmentEpoch,
                nextRevision,
                RetiredGraphReason.RecoveryPolicyNoLongerSelects,
                ComputeRetirementProof(prior.ProtectionDigest, selectionDigest, snapshot,
                    prior.EnrollmentEpoch, nextRevision)));
        }
        return retired;
    }

    private static PackageProtectionRecord CreateProtection(
        PackageProtectionRecord prior,
        StoreStateRecord next,
        PackageProtectionClosure active,
        PackageProtectionClosure recoverable,
        IReadOnlyList<RetiredGraphEvidence> retired,
        long revision)
    {
        var stateDigest = ProtectionDigest.StateBody(next);
        var candidate = new PackageProtectionRecord(prior.SchemaVersion, prior.RootIdentity,
            prior.EnrollmentEpoch, prior.MemberId, revision, stateDigest, new string('0', 64),
            active, recoverable, retired, legacyUnknownRecovery: false);
        return new PackageProtectionRecord(candidate.SchemaVersion, candidate.RootIdentity,
            candidate.EnrollmentEpoch, candidate.MemberId, candidate.Revision, candidate.StateBodyDigest,
            ProtectionDigest.Protection(candidate), candidate.ActiveClosure, candidate.RecoverableClosure,
            candidate.RetiredGraphs, candidate.LegacyUnknownRecovery);
    }

    private static string ComputeRetirementProof(
        string priorProtectionDigest,
        string nextSelectionDigest,
        ProtectedGraphSnapshot snapshot,
        long epoch,
        long revision)
    {
        using var stream = new MemoryStream();
        WriteProofString(stream, "Nuplane.RecoveryPolicyNoLongerSelects.v1");
        WriteProofString(stream, priorProtectionDigest);
        WriteProofString(stream, nextSelectionDigest);
        WriteProofString(stream, snapshot.SnapshotId.ToString("N"));
        WriteProofString(stream, snapshot.GraphId);
        WriteProofString(stream, snapshot.GenerationId);
        Span<byte> integer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(integer, epoch);
        stream.Write(integer);
        BinaryPrimitives.WriteInt64BigEndian(integer, revision);
        stream.Write(integer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteProofString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static IReadOnlyList<PackageInstallIdentity> GetUniqueIncomingInstalls(
        IReadOnlyList<IncomingSelection> incoming,
        PhysicalRootIdentity root)
    {
        var installs = new Dictionary<PackageVersionKey, PackageInstallIdentity>(PackageVersionKeyComparer.Instance);
        foreach (var selection in incoming)
        {
            foreach (var node in selection.Selection.Graph.Nodes)
            {
                var key = new PackageVersionKey(node.PackageId, node.Version);
                var identity = selection.Snapshot.Nodes.Single(snapshotNode =>
                    string.Equals(snapshotNode.Install.PackageId, node.PackageId, StringComparison.OrdinalIgnoreCase) &&
                    VersionEquals(snapshotNode.Install.Version, node.Version)).Install;
                if (installs.TryGetValue(key, out var prior) && prior != identity)
                    throw Refused("Successful graphs disagree on one selected package install identity.", root);
                installs[key] = identity;
            }
        }
        return installs.OrderBy(static pair => pair.Key.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pair => pair.Key.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => pair.Value).ToArray();
    }

    private static void ValidateAppliedPackages(
        IReadOnlyList<ResolvedPackage> appliedPackages,
        IReadOnlyList<IncomingSelection> activeSelections,
        PhysicalRootIdentity root)
    {
        var selected = new Dictionary<PackageVersionKey, PackageLineage>(PackageVersionKeyComparer.Instance);
        foreach (var selection in activeSelections)
        {
            foreach (var package in selection.Selection.Packages)
            {
                var key = new PackageVersionKey(package.Id, package.Version);
                var lineage = new PackageLineage(package.FeedName, package.SourceName,
                    selection.ExactPaths[key], package.PackageContentHash);
                if (selected.TryGetValue(key, out var prior) && prior != lineage)
                    throw Refused("Active resolved graphs disagree on one successfully applied package lineage.", root);
                selected[key] = lineage;
            }
        }

        foreach (var package in appliedPackages)
        {
            var key = new PackageVersionKey(package.Id, package.Version);
            if (!selected.TryGetValue(key, out var lineage) ||
                !string.Equals(package.InstallPath, lineage.InstallPath, StringComparison.Ordinal) ||
                !string.Equals(package.FeedName, lineage.FeedName, StringComparison.Ordinal) ||
                !string.Equals(package.SourceName, lineage.SourceName, StringComparison.Ordinal) ||
                !string.Equals(package.PackageContentHash, lineage.PackageContentHash, StringComparison.Ordinal))
            {
                throw Refused("Every successfully applied package must match a fully active native-bound graph selection.", root);
            }
        }
    }

    private static void ValidateSelections(
        IReadOnlyList<ResolvedPackageGraphSelection> selections,
        IReadOnlySet<string> desiredRoots,
        PhysicalRootIdentity root)
    {
        var graphIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineages = new Dictionary<PackageVersionKey, PackageLineage>(PackageVersionKeyComparer.Instance);
        foreach (var selection in selections)
        {
            if (selection is null || selection.Graph is null || selection.RootRequests.Count == 0 ||
                selection.Packages.Count != selection.Graph.Nodes.Count ||
                string.IsNullOrWhiteSpace(selection.Graph.GraphId) ||
                string.IsNullOrWhiteSpace(selection.Graph.GenerationId) ||
                string.IsNullOrWhiteSpace(selection.Graph.TargetFramework) ||
                !string.Equals(selection.Graph.GraphId, ResolvedPackageGraph.CreateGraphId(
                    selection.Graph.TargetFramework, selection.Graph.Roots, selection.Graph.Nodes,
                    selection.Graph.Edges, selection.Graph.SourceDecisions), StringComparison.Ordinal) ||
                !graphIds.Add(selection.Graph.GraphId))
            {
                throw Refused("Resolved graph selections must have complete canonical graph and generation identities and unique graph IDs.", root);
            }
            var packages = new Dictionary<PackageVersionKey, ResolvedPackage>(PackageVersionKeyComparer.Instance);
            foreach (var package in selection.Packages)
            {
                if (package is null || string.IsNullOrWhiteSpace(package.Id) || string.IsNullOrWhiteSpace(package.Version) ||
                    string.IsNullOrWhiteSpace(package.InstallPath) ||
                    !packages.TryAdd(new PackageVersionKey(package.Id, package.Version), package))
                {
                    throw Refused("A selected package projection contains a blank or duplicate package identity.", root);
                }
            }
            foreach (var node in selection.Graph.Nodes)
            {
                var key = new PackageVersionKey(node.PackageId, node.Version);
                if (!packages.TryGetValue(key, out var package) ||
                    !string.Equals(package.InstallPath, node.InstallPath, StringComparison.Ordinal) ||
                    !string.Equals(package.PackageContentHash, node.PackageContentHash, StringComparison.Ordinal) ||
                    !string.Equals(string.IsNullOrWhiteSpace(package.SourceName) ? package.FeedName : package.SourceName,
                        node.SourceName, StringComparison.Ordinal))
                {
                    throw Refused("A resolved package projection does not match its graph node and original install path.", root);
                }
                var lineage = new PackageLineage(package.FeedName, package.SourceName, package.InstallPath, package.PackageContentHash);
                if (lineages.TryGetValue(key, out var prior) && prior != lineage)
                    throw Refused("Resolved graph selections disagree on the package's exact source lineage.", root);
                lineages[key] = lineage;
            }
            foreach (var request in selection.RootRequests)
            {
                if (request is null || !desiredRoots.Contains(request.Id))
                    throw Refused("A resolved graph root is outside the cycle's declared desired-root set.", root);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> CopyMap(
        IReadOnlyDictionary<string, string> values,
        string description,
        PhysicalRootIdentity root)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value) || !copy.TryAdd(key, value))
                throw Refused($"The {description} contains a blank or ambiguous entry.", root);
        }
        return copy;
    }

    private static IReadOnlySet<string> CopySet(IReadOnlySet<string> values, string description, PhysicalRootIdentity root)
    {
        var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw Refused($"The {description} contains an empty package identifier.", root);
            copy.Add(value);
        }
        return copy;
    }

    private static bool IsFullyActive(ResolvedPackageGraph graph, IReadOnlyDictionary<string, string> active)
        => graph.Nodes.Count > 0 && graph.Nodes.All(node => active.TryGetValue(node.PackageId, out var version) && VersionEquals(version, node.Version));

    private static bool IsFullyActive(ProtectedGraphSnapshot snapshot, IReadOnlyDictionary<string, string> active)
        => snapshot.Nodes.Count > 0 && snapshot.Nodes.All(node => active.TryGetValue(node.Install.PackageId, out var version) && VersionEquals(version, node.Install.Version));

    private static IReadOnlySet<string> GetRootPackageIds(ProtectedGraphSnapshot snapshot)
    {
        var installs = snapshot.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
        return snapshot.RequestedRoots.Select(selection => installs[selection.SelectedNodeId].PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool EquivalentSelection(ProtectedGraphSnapshot prior, ProtectedGraphSnapshot candidate)
    {
        if (prior.Disposition != candidate.Disposition || prior.Nodes.Count != candidate.Nodes.Count ||
            prior.Edges.Count != candidate.Edges.Count || prior.RequestedRoots.Count != candidate.RequestedRoots.Count)
            return false;
        var priorByKey = prior.Nodes.ToDictionary(static node => new PackageVersionKey(node.Install.PackageId, node.Install.Version),
            static node => node, PackageVersionKeyComparer.Instance);
        var candidateByKey = candidate.Nodes.ToDictionary(static node => new PackageVersionKey(node.Install.PackageId, node.Install.Version),
            static node => node, PackageVersionKeyComparer.Instance);
        if (priorByKey.Count != candidateByKey.Count || candidateByKey.Any(pair =>
                !priorByKey.TryGetValue(pair.Key, out var old) || old.Install != pair.Value.Install))
            return false;

        static string RequestKey(PackageGraphRootSelection selection, IReadOnlyDictionary<Guid, PackageInstallIdentity> installs)
        {
            var request = selection.Request;
            var install = installs[selection.SelectedNodeId];
            return string.Join('\u001f', request.Id, request.VersionRange, request.FeedName, request.UpdatePolicy,
                request.SourceName, install.PackageId, install.Version);
        }
        var priorInstalls = prior.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
        var candidateInstalls = candidate.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
        var oldRequests = prior.RequestedRoots.Select(item => RequestKey(item, priorInstalls)).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var newRequests = candidate.RequestedRoots.Select(item => RequestKey(item, candidateInstalls)).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        if (!oldRequests.SequenceEqual(newRequests, StringComparer.Ordinal))
            return false;

        static string EdgeKey(PackageGraphEdgeIdentity edge, IReadOnlyDictionary<Guid, PackageInstallIdentity> installs)
        {
            var from = installs[edge.FromNodeId];
            var to = installs[edge.ToNodeId];
            return string.Join('\u001f', from.PackageId, from.Version, to.PackageId, to.Version,
                edge.RequestedPackageId, edge.RequestedVersionRange, edge.TargetFramework, edge.IsOptional);
        }
        var oldEdges = prior.Edges.Select(item => EdgeKey(item, priorInstalls)).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        var newEdges = candidate.Edges.Select(item => EdgeKey(item, candidateInstalls)).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        return oldEdges.SequenceEqual(newEdges, StringComparer.Ordinal);
    }

    private static bool ActivationRecordMatchesSnapshot(GraphActivationRecord record, ProtectedGraphSnapshot snapshot)
    {
        if (!GraphKeyMatches(record, KeyFor(snapshot)) || record.Status != GraphActivationStatus.Active || record.Failure is not null)
            return false;
        var roots = GetRootPackageIds(snapshot);
        var nodes = snapshot.Nodes.ToDictionary(static node => node.Install.PackageId, static node => node.Install.Version,
            StringComparer.OrdinalIgnoreCase);
        return SetEquals(record.RootPackageIds, roots) && SetEquals(record.NodePackageIds, nodes.Keys) &&
               record.NodeVersionsByPackageId is { } versions && VersionMapsEqual(versions, nodes);
    }

    private static bool GraphKeyMatches(GraphActivationRecord record, GraphKey key)
        => string.Equals(record.GraphId, key.GraphId, StringComparison.Ordinal) &&
           string.Equals(record.GenerationId, key.GenerationId, StringComparison.Ordinal);

    private static bool SetEquals(IEnumerable<string>? left, IEnumerable<string> right)
    {
        if (left is null)
            return false;
        var items = left.ToArray();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items.All(item => !string.IsNullOrWhiteSpace(item) && unique.Add(item)) && unique.SetEquals(right);
    }

    private static bool VersionMapsEqual(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in actual)
            if (!copy.TryAdd(key, value)) return false;
        return copy.Count == expected.Count && expected.All(pair => copy.TryGetValue(pair.Key, out var version) && VersionEquals(version, pair.Value));
    }

    private static void AddSnapshot(
        IDictionary<GraphKey, ProtectedGraphSnapshot> snapshots,
        ProtectedGraphSnapshot snapshot,
        PhysicalRootIdentity root)
    {
        var key = KeyFor(snapshot);
        if (snapshots.TryGetValue(key, out var previous))
        {
            if (!previous.HasSamePayloadAs(snapshot))
                throw Refused("Two transitions selected conflicting payloads for one graph generation.", root);
            return;
        }
        snapshots.Add(key, snapshot);
    }

    private static GraphKey KeyFor(ProtectedGraphSnapshot snapshot) => new(snapshot.GraphId, snapshot.GenerationId);
    private static bool VersionEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root = null) =>
        new(PackageStoreAdmissionReason.StateMismatch, message, root);

    private readonly record struct GraphKey(string GraphId, string GenerationId);
    private readonly record struct PackageVersionKey(string PackageId, string Version);
    private sealed class PackageVersionKeyComparer : IEqualityComparer<PackageVersionKey>
    {
        internal static PackageVersionKeyComparer Instance { get; } = new();
        public bool Equals(PackageVersionKey x, PackageVersionKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.PackageId, y.PackageId) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Version, y.Version);
        public int GetHashCode(PackageVersionKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PackageId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version));
    }

    private sealed record PackageLineage(string? FeedName, string? SourceName, string InstallPath, string? PackageContentHash);

    private sealed class DescriptorProjection(
        PackageInstallIdentity install,
        PackageLineage lineage,
        GraphKey graph,
        ActivePackageRole role,
        IEnumerable<string> roots,
        IEnumerable<string> dependencies)
    {
        internal PackageInstallIdentity Install { get; } = install;
        internal PackageLineage Lineage { get; } = lineage;
        internal ActivePackageRole Role { get; private set; } = role;
        internal HashSet<string> Roots { get; } = new(roots, StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Dependencies { get; } = new(dependencies, StringComparer.OrdinalIgnoreCase);
        internal HashSet<GraphKey> Graphs { get; } = [graph];

        internal void Merge(DescriptorProjection other, PhysicalRootIdentity root)
        {
            if (Install != other.Install || Lineage != other.Lineage)
                throw Refused("A package ID has conflicting native identity or source lineage across final graphs.", root);
            if (Role != other.Role)
                Role = ActivePackageRole.RootAndDependency;
            Roots.UnionWith(other.Roots);
            Dependencies.UnionWith(other.Dependencies);
            Graphs.UnionWith(other.Graphs);
        }
    }

    private sealed record IncomingSelection(
        ResolvedPackageGraphSelection Selection,
        ProtectedGraphSnapshot Snapshot,
        IReadOnlyDictionary<PackageVersionKey, string> ExactPaths);
}
