using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class CoordinatedActiveStateTransitionProducerTests
{
    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_PublishesSuccessfulRootBesideExactFailedRootSubclosure()
    {
        using var context = await CreateCompleteWithTwoRootHistoryAsync();
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        var admission = new PackageStoreAdmission(context.Files, registry, context.Fixture.PackageInstallRoot);
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, operation.Status);
        using var borrow = operation.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, context.StatePaths["first"]);
        var prior = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var priorSnapshot = Assert.Single(prior.ProtectionRecord!.ActiveClosure.Graphs!);
        var incomingGraph = context.Graphs["first"];
        var packages = PackagesFor(incomingGraph, prior);
        var now = DateTimeOffset.UtcNow;
        var candidate = await CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
            context.Files,
            registry,
            stateRegistry,
            borrow,
            prior.ActiveVersionById,
            [],
            new PackageChangeSet([], [], [], "successful-root-with-failed-root", now),
            [new ResolvedPackageGraphSelection(incomingGraph, context.Requests["first"], packages)],
            Set("Root.First", "Shared.Dependency"),
            Set("Shared.Dependency"),
            "successful-root-with-failed-root",
            now,
            CancellationToken.None);

        await stateRegistry.PersistCoordinatedActiveStateAsync(borrow, candidate, CancellationToken.None);
        var reopened = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var verified = PersistedStoreStateGraphVerifier.Verify(reopened);
        Assert.Equal(2, reopened.ProtectionRecord!.Revision);
        Assert.Equal(2, verified.ActiveGraphs.Count);
        Assert.Equal(2, verified.RecoverableGraphs.Count);
        var recoverableById = reopened.ProtectionRecord.RecoverableClosure.Graphs!
            .ToDictionary(static graph => graph.SnapshotId);
        foreach (var graph in reopened.ProtectionRecord.ActiveClosure.Graphs!)
            Assert.True(graph.HasSamePayloadAs(recoverableById[graph.SnapshotId]));

        var activeSuccess = verified.ActiveGraphs.Single(graph => graph.GraphId == incomingGraph.GraphId);
        Assert.Equal(new[] { "Root.First" }, activeSuccess.RequestedRoots.Select(root =>
            activeSuccess.Nodes.Single(node => node.NodeId == root.SelectedNodeId).Install.PackageId));
        var retainedFailure = verified.ActiveGraphs.Single(graph => graph.GraphId != incomingGraph.GraphId);
        Assert.Equal(new[] { "Shared.Dependency" }, retainedFailure.RequestedRoots.Select(root =>
            retainedFailure.Nodes.Single(node => node.NodeId == root.SelectedNodeId).Install.PackageId));
        Assert.Equal(new[] { "Shared.Dependency" }, retainedFailure.Nodes.Select(static node => node.Install.PackageId));
        Assert.Empty(retainedFailure.Edges);
        Assert.Equal(priorSnapshot.GenerationId, retainedFailure.GenerationId);

        var retired = Assert.Single(reopened.ProtectionRecord.RetiredGraphs);
        Assert.Equal(priorSnapshot.SnapshotId, retired.SnapshotId);
        Assert.Equal(RetiredGraphReason.RecoveryPolicyNoLongerSelects, retired.Reason);
        Assert.Equal(2, retired.RetiringRevision);
        Assert.Matches("^[0-9a-f]{64}$", retired.ProofDigest);

        var shared = reopened.ActivePackageDescriptorsByIdNormalized["Shared.Dependency"];
        Assert.Equal(ActivePackageRole.RootAndDependency, shared.PackageRole);
        Assert.Equal(new[] { "Root.First", "Shared.Dependency" }, shared.RootPackageIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Root.First" }, shared.DependencyOfPackageIds);
        Assert.Equal(prior.ActivePackageDescriptorsByIdNormalized["Shared.Dependency"].InstallPath, shared.InstallPath);
        Assert.Equal(2, reopened.ActiveGraphsByIdNormalized.Count);
        var firstAcknowledgement = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
            PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.Members
                .Single(member => member.MemberId == "first").Binding);
        Assert.Equal(2, firstAcknowledgement.ProtectionRecord.Revision);
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_RejectsSelectionThatDisagreesWithGraphSourceLineage()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        var admission = new PackageStoreAdmission(context.Files, registry, context.Fixture.PackageInstallRoot);
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, context.StatePaths["first"]);
        var prior = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var graph = context.Graphs["first"];
        var packages = PackagesFor(graph, prior).Select(package => package.Id == "Root.First"
            ? package with { SourceName = "different-source" }
            : package).ToArray();
        var now = DateTimeOffset.UtcNow;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
                context.Files,
                registry,
                stateRegistry,
                borrow,
                prior.ActiveVersionById,
                [],
                new PackageChangeSet([], [], [], "mismatched-source", now),
                [new ResolvedPackageGraphSelection(graph, context.Requests["first"], packages)],
                Set("Root.First"),
                Set(),
                "mismatched-source",
                now,
                CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        var after = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.True(prior.ProtectionRecord!.HasSamePayloadAs(after.ProtectionRecord!));
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_RejectsAppliedPathThatDisagreesWithNativeBoundSelection()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        var admission = new PackageStoreAdmission(context.Files, registry, context.Fixture.PackageInstallRoot);
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, context.StatePaths["first"]);
        var prior = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var graph = context.Graphs["first"];
        var packages = PackagesFor(graph, prior);
        var applied = packages[0] with { InstallPath = packages[0].InstallPath + ".unobserved" };
        var now = DateTimeOffset.UtcNow;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
                context.Files,
                registry,
                stateRegistry,
                borrow,
                prior.ActiveVersionById,
                [applied],
                new PackageChangeSet([], [], [], "applied-path-mismatch", now),
                [new ResolvedPackageGraphSelection(graph, context.Requests["first"], packages)],
                Set("Root.First"),
                Set(),
                "applied-path-mismatch",
                now,
                CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        var after = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.True(prior.ProtectionRecord!.HasSamePayloadAs(after.ProtectionRecord!));
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_RejectsDesiredRootMissingSelectionAndFailureEvidence()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        var admission = new PackageStoreAdmission(context.Files, registry, context.Fixture.PackageInstallRoot);
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, context.StatePaths["first"]);
        var prior = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var graph = context.Graphs["first"];
        var now = DateTimeOffset.UtcNow;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
                context.Files, registry, stateRegistry, borrow, prior.ActiveVersionById, [],
                new PackageChangeSet([], [], [], "missing-desired-root", now),
                [new ResolvedPackageGraphSelection(graph, context.Requests["first"], PackagesFor(graph, prior))],
                Set("Root.First", "Shared.Dependency"), Set(), "missing-desired-root", now,
                CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.Contains("Every desired root", refusal.Message, StringComparison.Ordinal);
        var after = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.True(prior.ProtectionRecord!.HasSamePayloadAs(after.ProtectionRecord!));
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_RejectsInactiveSelectedGenerationInsteadOfRetainingPriorAsSuccess()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        var admission = new PackageStoreAdmission(context.Files, registry, context.Fixture.PackageInstallRoot);
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, context.StatePaths["first"]);
        var prior = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var original = context.Graphs["first"];
        var nodes = original.Nodes.Select(node => node.PackageId == "Root.First"
            ? node with { Version = "2.0.0", InstallPath = Path.Combine(Path.GetDirectoryName(node.InstallPath!)!, "2.0.0") }
            : node).ToArray();
        var roots = nodes.Where(static node => node.PackageId == "Root.First").ToArray();
        var edges = original.Edges.Select(edge => edge.FromPackageId == "Root.First"
            ? edge with { FromVersion = "2.0.0" }
            : edge).ToArray();
        var sources = original.SourceDecisions.Select(source => source.PackageId == "Root.First"
            ? source with { SelectedVersion = "2.0.0" }
            : source).ToArray();
        var selected = original with
        {
            GraphId = ResolvedPackageGraph.CreateGraphId(original.TargetFramework, roots, nodes, edges, sources),
            GenerationId = original.GenerationId + "-unactivated-v2",
            Roots = roots,
            Nodes = nodes,
            Edges = edges,
            SourceDecisions = sources
        };
        var now = DateTimeOffset.UtcNow;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            CoordinatedActiveStateTransitionProducer.CreateCandidateAsync(
                context.Files, registry, stateRegistry, borrow, prior.ActiveVersionById, [],
                new PackageChangeSet([], [], [], "inactive-selected-generation", now),
                [new ResolvedPackageGraphSelection(selected, context.Requests["first"], PackagesFor(selected, prior))],
                Set("Root.First"), Set(), "inactive-selected-generation", now, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.Contains("fully active selected graph", refusal.Message, StringComparison.Ordinal);
        var after = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        Assert.True(prior.ProtectionRecord!.HasSamePayloadAs(after.ProtectionRecord!));
    }

    private static async Task<RootMembershipProtectionVerificationTests.Context> CreateCompleteWithTwoRootHistoryAsync()
    {
        var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        try
        {
            var original = context.Graphs["first"];
            var shared = original.Nodes.Single(static node => node.PackageId == "Shared.Dependency");
            var nodes = original.Nodes.Select(node => node == shared
                ? node with { Role = PackageNodeRole.RootAndDependency }
                : node).ToArray();
            var firstRoot = nodes.Single(static node => node.PackageId == "Root.First");
            var sharedRoot = nodes.Single(static node => node.PackageId == "Shared.Dependency");
            var roots = new[] { firstRoot, sharedRoot };
            var graphId = ResolvedPackageGraph.CreateGraphId(original.TargetFramework, roots, nodes,
                original.Edges, original.SourceDecisions);
            var priorGraph = original with
            {
                GraphId = graphId,
                GenerationId = original.GenerationId + "-with-shared-root",
                Roots = roots,
                Nodes = nodes
            };
            var priorRequests = context.Requests["first"].Append(
                new PackageRequest("Shared.Dependency", string.Empty, "feed", PackageUpdatePolicy.Range, "dependency"))
                .ToArray();
            var priorState = context.States["first"];
            var packages = PackagesFor(priorGraph, priorState);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                StoreStateRecord.Empty(), [priorGraph], priorState.ActiveVersionById,
                "two-root-prior", DateTimeOffset.UnixEpoch);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
                StoreStateRecord.Empty(), priorState.ActiveVersionById, packages,
                new PackageChangeSet(packages, [], [], "two-root-prior", DateTimeOffset.UnixEpoch),
                "two-root-prior", DateTimeOffset.UnixEpoch, [priorGraph], graphRecords);
            var installs = ObserveInstalls(context, priorGraph);
            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                priorGraph, priorRequests, installs, priorState.ProtectionRecord!.Revision);
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            var nextState = priorState with
            {
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors,
                    StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords,
                    StringComparer.OrdinalIgnoreCase)
            };
            var previous = priorState.ProtectionRecord;
            var placeholder = new PackageProtectionRecord(1, context.RootIdentity, 1, previous.MemberId,
                previous.Revision, new string('0', 64), new string('0', 64), closure, closure, [], false);
            context.States["first"] = context.Reprotect(nextState with { ProtectionRecord = placeholder }, false,
                previous.Revision);

            await context.PublishStatesAsync();
            await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1, true,
                CancellationToken.None);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static ResolvedPackage[] PackagesFor(ResolvedPackageGraph graph, StoreStateRecord state)
        => graph.Nodes.Select(node =>
        {
            var descriptor = state.ActivePackageDescriptorsByIdNormalized[node.PackageId];
            return new ResolvedPackage(node.PackageId, node.Version, descriptor.FeedName ?? "feed",
                node.InstallPath!, descriptor.ActivatedAtUtc, descriptor.SourceName ?? node.SourceName ?? string.Empty)
            {
                PackageContentHash = node.PackageContentHash
            };
        }).ToArray();

    private static PackageInstallIdentity[] ObserveInstalls(
        RootMembershipProtectionVerificationTests.Context context,
        ResolvedPackageGraph graph)
    {
        var reader = new PackageInstallIdentityReader(context.Files);
        return graph.Nodes.Select(node =>
        {
            var relative = Path.GetRelativePath(context.Fixture.PackageInstallRoot, node.InstallPath!)
                .Replace(Path.DirectorySeparatorChar, '/');
            using var observation = reader.Observe(context.Root, context.RootIdentity, relative,
                node.PackageId, node.Version, node.PackageContentHash);
            return observation.InstallIdentity;
        }).ToArray();
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
