using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Events;
using Nuplane.Hosting;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Observability;
using Nuplane.Registration;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    public enum UnknownParticipant
    {
        DiffEngine,
        DryRunPlanner,
        DryRunPlannerWithUnknownNestedDiff,
        LockFileCoordinator,
        RetryPolicy
    }

    [Fact]
    public async Task TwoAddNuplaneProviders_SerializeScopedSourceReadsAndPublishFailureAndStateUnderTheirMembers()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var initialFirst = fixture.States["first"];
        var initialSecond = fixture.States["second"];
        var firstEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSource = new ScopedFileSource(fixture.SharedInstallPath, firstEntered, releaseFirst.Task,
            failAfterRead: false);
        var secondSource = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null,
            failAfterRead: true);

        using var firstProvider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], firstSource);
        using var secondProvider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["second"], secondSource);
        var firstService = firstProvider.GetRequiredService<IReconciliationService>();
        var secondService = secondProvider.GetRequiredService<IReconciliationService>();
        var firstCycle = firstService.TriggerAsync(ReconciliationTrigger.Manual("first-cycle"), CancellationToken.None);

        try
        {
            Assert.Equal("shared sentinel", await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            // The first scoped callback has already proved the root and both state slots are owned.
            // Cancellation is the bounded observation that the second independent provider cannot
            // reach its callback while that ownership remains live. The native lock may fail closed
            // immediately with a typed busy refusal instead of waiting for cancellation.
            using var blockedAttempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var blockedResult = await Record.ExceptionAsync(() =>
                secondService.TriggerAsync(ReconciliationTrigger.Manual("blocked-cycle"), blockedAttempt.Token));
            Assert.True(blockedResult is OperationCanceledException ||
                blockedResult is PackageStoreAdmissionException { Reason: PackageStoreAdmissionReason.UnknownAuthority },
                $"Expected a bounded cancellation or native root-lock busy refusal, got {blockedResult?.GetType().Name ?? "no exception"}.");
            Assert.Equal(0, secondSource.CallbackCount);
            Assert.Equal(0, secondSource.SentinelReadCount);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await firstCycle;
        await secondService.TriggerAsync(ReconciliationTrigger.Manual("second-cycle"), CancellationToken.None);

        Assert.Equal(1, firstSource.CallbackCount);
        Assert.Equal(1, firstSource.SentinelReadCount);
        Assert.Equal(1, secondSource.CallbackCount);
        Assert.Equal(1, secondSource.SentinelReadCount);

        var firstAfter = await fixture.ReadFreshStateAsync("first");
        var secondAfter = await fixture.ReadFreshStateAsync("second");
        Assert.Equal(initialFirst.ActiveVersionById, firstAfter.ActiveVersionById);
        Assert.Equal(initialSecond.ActiveVersionById, secondAfter.ActiveVersionById);
        Assert.Equal(initialFirst.ProtectionRecord!.Revision + 2, firstAfter.ProtectionRecord!.Revision);
        Assert.Equal(initialSecond.ProtectionRecord!.Revision + 2, secondAfter.ProtectionRecord!.Revision);
        Assert.Single(firstAfter.LastSuccessfulSourceSnapshots);
        Assert.Empty(secondAfter.LastSuccessfulSourceSnapshots);
        var failure = Assert.Single(secondAfter.LastFailureById.Values);
        Assert.Equal("source-read", failure.Stage);
        Assert.Equal("second-cycle", failure.CorrelationId);
    }

    [Fact]
    public async Task ThirdUnlistedStateSlot_IsRefusedBeforeSourceCallback()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var unlistedStatePath = Path.Combine(fixture.Temp.Path, "third-state", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unlistedStatePath)!);
        var source = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null, failAfterRead: false);
        using var provider = CreateProvider(fixture.PackageInstallRoot, unlistedStatePath, source);
        var service = provider.GetRequiredService<IReconciliationService>();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.TriggerAsync(ReconciliationTrigger.Manual("unlisted-state"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);
    }

    [Fact]
    public async Task UnscopedCustomSource_IsRefusedBeforeAnyCallback()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new UnscopedSourceProbe();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source);
        var service = provider.GetRequiredService<IReconciliationService>();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            service.TriggerAsync(ReconciliationTrigger.Manual("unscoped-source"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(fixture.States["first"].ProtectionRecord!.Revision, after.ProtectionRecord!.Revision);
    }

    [Fact]
    public async Task EnabledManifestInsideEnrolledInstall_ReadsUnderOriginalOwnerAndPublishesSnapshot()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = true;
        options.Manifest.Path = Path.Combine(fixture.SharedInstallPath, "desired.json");
        await File.WriteAllTextAsync(options.Manifest.Path,
            """{"schemaVersion":"1.0","packages":[]}""");
        var nativeReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNativeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DesiredManifestPackageSource(new DesiredManifestReader(async token =>
        {
            nativeReadStarted.TrySetResult();
            await releaseNativeRead.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }), options);
        var companion = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null, failAfterRead: false);
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            additionalSource: companion);
        var service = provider.GetRequiredService<IReconciliationService>();
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var cycle = service.TriggerAsync(ReconciliationTrigger.Manual("enrolled-manifest"), CancellationToken.None);

        try
        {
            await nativeReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance));
            Assert.Null(source.LastReadResult);
        }
        finally
        {
            releaseNativeRead.TrySetResult();
            await cycle;
        }

        Assert.Equal(ManifestReadStatus.Succeeded, source.LastReadResult!.Status);
        Assert.Empty(source.LastReadResult.Manifest!.Packages);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(fixture.States["first"].ActiveVersionById, after.ActiveVersionById);
        Assert.Equal(fixture.States["first"].ProtectionRecord!.Revision + 3, after.ProtectionRecord!.Revision);
        Assert.Equal(
            new[] { typeof(ScopedFileSource).FullName!, typeof(DesiredManifestPackageSource).FullName! }.Order(StringComparer.Ordinal),
            after.LastSuccessfulSourceSnapshots.Keys.Order(StringComparer.Ordinal));
        Assert.All(after.LastSuccessfulSourceSnapshots.Values, snapshot =>
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<PackageRequest>>(snapshot.Requests)));
        Assert.Equal(1, companion.CallbackCount);
        Assert.Equal(1, companion.SentinelReadCount);

        // Enrolled callers still need the original counted borrow; a path-only call cannot reuse it.
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => source.GetDesiredAsync(CancellationToken.None));
        await using var afterDrain = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
    }

    [Fact]
    public async Task AddNuplane_ProtectedTransitionPublishesExactSuccessfulGraph()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var freshRootPath = fixture.Install("Fresh.Root", "1.0.0", "Shared.Dependency");
        var request = new PackageRequest("Fresh.Root", "[1.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "fresh-root");
        var packages = new[]
        {
            new ResolvedPackage("Fresh.Root", "1.0.0", "test-feed", freshRootPath,
                DateTimeOffset.UnixEpoch, "fresh-root"),
            new ResolvedPackage("Shared.Dependency", "2.1.0", "test-feed", fixture.SharedInstallPath,
                DateTimeOffset.UnixEpoch, "dependency")
        };
        var source = new RootRequestSource(request, freshRootPath);
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            preserveActiveDiff: false, dispatcherOverride: dispatcher,
            resolverOverride: new MapScopedResolver(packages), lockFileMode: LockFileMode.Enforce);

        await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("protected-transition"), CancellationToken.None);

        var state = await fixture.ReadFreshStateAsync("first");
        Assert.Equal("1.0.0", state.ActiveVersionById["Fresh.Root"]);
        Assert.False(state.ActiveVersionById.ContainsKey("Root.First"));
        Assert.True(state.ProtectionRecord!.Revision > fixture.States["first"].ProtectionRecord!.Revision);
        var activeGraph = Assert.Single(PersistedStoreStateGraphVerifier.Verify(state).ActiveGraphs);
        Assert.Equal("Fresh.Root", Assert.Single(activeGraph.RequestedRoots).Request.Id);
        Assert.Equal(new[] { "Fresh.Root", "Shared.Dependency" }, activeGraph.Nodes
            .Select(static node => node.Install.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, dispatcher.ChangingCount);
    }

    [Fact]
    public async Task CoordinatedDiffPreflight_RefusesOmittedGenerationBeforeChanging()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var rootPath = fixture.Install("Fresh.Root", "1.0.0", dependencyId: null);
        var request = new PackageRequest("Fresh.Root", "[1.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "fresh-root");
        var package = new ResolvedPackage("Fresh.Root", "1.0.0", "test-feed", rootPath,
            DateTimeOffset.UnixEpoch, "fresh-root");
        var node = new ResolvedPackageNode("Fresh.Root", "1.0.0", PackageNodeRole.Root,
            rootPath, PackageSourceKind.RemoteFeed, "fresh-root", null, [], [], []);
        var graph = new ResolvedPackageGraph(
            ResolvedPackageGraph.CreateGraphId("net10.0", [node], [node], [], []),
            string.Empty,
            "net10.0",
            [node], [node], [], [], DateTimeOffset.UnixEpoch);
        var selection = new ResolvedPackageGraphSelection(graph, [request], [package]);
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new RootRequestSource(request, rootPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher, lockFileMode: LockFileMode.Enforce);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        var packageAdmission = Assert.IsType<PackageStoreAdmission>(admission);
        var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
        var driver = new CoordinatedActiveStateTransitionDriver(
            provider.GetRequiredService<IPhysicalStoreFileSystem>(), packageAdmission.Registry, registry);
        var middleware = new DiffAndChangeEventMiddleware(
            provider.GetRequiredService<IDesiredActualDiffEngine>(),
            provider.GetRequiredService<IDryRunPlanner>(),
            provider.GetRequiredService<IReconciliationRetryPolicy>(),
            provider.GetRequiredService<IStoreRegistry>(),
            dispatcher,
            provider.GetRequiredService<ReconciliationMetrics>(),
            driver);
        var context = new ReconciliationCycleContext
        {
            CorrelationId = "omitted-generation",
            CycleStartedAt = DateTimeOffset.UnixEpoch,
            CancellationToken = CancellationToken.None,
            PackageStoreOwner = owner,
            DesiredRequests = [request],
            ResolutionResult = new([package], [], [], [graph])
            {
                GraphSelections = [selection]
            }
        };
        var downstreamReached = false;

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => middleware.InvokeAsync(
            context,
            () =>
            {
                downstreamReached = true;
                return Task.CompletedTask;
            }));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.Equal(0, dispatcher.ChangingCount);
        Assert.False(downstreamReached);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.True(fixture.States["first"].ProtectionRecord!.ActiveClosure
            .HasSamePayloadAs(after.ProtectionRecord!.ActiveClosure));
    }

    [Fact]
    public async Task AddNuplane_LockFailedDesiredRootRetainsItsExactPriorSubclosure()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var priorState = fixture.States["first"];
        var priorSnapshot = Assert.Single(priorState.ProtectionRecord!.ActiveClosure.Graphs!);
        var updatedRootPath = fixture.Install("Root.First", "2.0.0", "Shared.Dependency");
        var request = new PackageRequest("Root.First", "[2.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "first");
        var packages = new[]
        {
            new ResolvedPackage("Root.First", "2.0.0", "test-feed", updatedRootPath,
                DateTimeOffset.UnixEpoch, "first"),
            new ResolvedPackage("Shared.Dependency", "2.1.0", "test-feed", fixture.SharedInstallPath,
                DateTimeOffset.UnixEpoch, "dependency")
        };
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new RootRequestSource(request, updatedRootPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher, resolverOverride: new MapScopedResolver(packages),
            lockFileCoordinatorOverride: new RejectRootLockFileCoordinator("Root.First"));

        await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("failed-root-retention"), CancellationToken.None);

        var state = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(priorState.ActiveVersionById, state.ActiveVersionById);
        Assert.True(state.ProtectionRecord!.Revision > priorState.ProtectionRecord!.Revision);
        var retained = Assert.Single(state.ProtectionRecord.ActiveClosure.Graphs!);
        Assert.True(priorSnapshot.HasSamePayloadAs(retained));
        Assert.Equal(0, dispatcher.ChangingCount);
    }

    [Fact]
    public async Task CoordinatedApplyFailure_RestoresExactPriorSubclosureAndPublishesIndependentGraph()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var priorState = fixture.States["first"];
        var priorSnapshot = Assert.Single(priorState.ProtectionRecord!.ActiveClosure.Graphs!);
        var changedRootPath = fixture.Install("Root.First", "2.0.0", "New.Dependency");
        var newDependencyPath = fixture.Install("New.Dependency", "1.0.0", dependencyId: null);
        var independentRootPath = fixture.Install("Independent.Root", "1.0.0", dependencyId: null);
        var changedRootRequest = new PackageRequest("Root.First", "[2.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "changed-root");
        var independentRootRequest = new PackageRequest("Independent.Root", "[1.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "independent-root");
        var actualHash = "sha512:" + Convert.ToBase64String(new byte[64]);
        var mismatchedExpectedHash = "sha512:" + Convert.ToBase64String(Enumerable.Repeat((byte)1, 64).ToArray());
        var changedRoot = new ResolvedPackage("Root.First", "2.0.0", "test-feed", changedRootPath,
            DateTimeOffset.UnixEpoch, "changed-root") { PackageContentHash = actualHash };
        var newDependency = new ResolvedPackage("New.Dependency", "1.0.0", "test-feed", newDependencyPath,
            DateTimeOffset.UnixEpoch, "dependency-of:Root.First") { PackageContentHash = actualHash };
        var independentRoot = new ResolvedPackage("Independent.Root", "1.0.0", "test-feed", independentRootPath,
            DateTimeOffset.UnixEpoch, "independent-root") { PackageContentHash = actualHash };
        var changedRootSelection = CreateSelection(changedRootRequest, changedRoot, newDependency);
        var independentSelection = CreateSelection(independentRootRequest, independentRoot);
        var resolution = new PackageResolutionResult(
            [changedRoot, newDependency, independentRoot], [], [],
            [changedRootSelection.Graph, independentSelection.Graph])
        {
            GraphSelections = [changedRootSelection, independentSelection],
            ExpectedArtifactHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["New.Dependency"] = mismatchedExpectedHash
            }
        };

        var conflictingActiveMap = new Dictionary<string, string>(priorState.ActiveVersionById,
            StringComparer.OrdinalIgnoreCase)
        {
            ["Shared.Dependency"] = "99.0.0"
        };
        var conflict = Assert.Throws<PackageStoreAdmissionException>(() =>
            ActivePackageCatalogMapper.RestoreFailedRootSubclosureVersions(
                priorState, conflictingActiveMap, new HashSet<string>(["Root.First"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["Root.First"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, conflict.Reason);

        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new MutableRootRequestSource(fixture.SharedInstallPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher);
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        var packageAdmission = Assert.IsType<PackageStoreAdmission>(admission);
        var stateRegistry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(
            provider.GetRequiredService<IStoreRegistry>());
        var boundStoreRegistry = CoordinatedReconciliationAdapters.BindStoreRegistry(stateRegistry, owner);
        var transitionDriver = new CoordinatedActiveStateTransitionDriver(
            provider.GetRequiredService<IPhysicalStoreFileSystem>(), packageAdmission.Registry, stateRegistry);
        var diffEngine = provider.GetRequiredService<IDesiredActualDiffEngine>();
        var failureRecorder = CoordinatedReconciliationAdapters.BindFailureRecorder(
            provider.GetRequiredService<IFailureRecorder>(), owner);
        var applyExecutor = new PackageApplyExecutor(
            provider.GetRequiredService<IPackageResolver>(),
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), failureRecorder),
            provider.GetRequiredService<IReconciliationRetryPolicy>(),
            failureRecorder);
        var cleanupService = new CapturingPackageCleanupService();
        var context = new ReconciliationCycleContext
        {
            CorrelationId = "apply-failure-retention",
            CycleStartedAt = DateTimeOffset.UnixEpoch,
            CancellationToken = CancellationToken.None,
            PackageStoreOwner = owner,
            DesiredRequests = [changedRootRequest, independentRootRequest],
            ResolutionResult = resolution
        };
        var diffMiddleware = new DiffAndChangeEventMiddleware(
            diffEngine,
            provider.GetRequiredService<IDryRunPlanner>(),
            provider.GetRequiredService<IReconciliationRetryPolicy>(),
            boundStoreRegistry,
            dispatcher,
            provider.GetRequiredService<ReconciliationMetrics>(),
            transitionDriver);
        var transactionMiddleware = new TransactionExecutionMiddleware(
            applyExecutor, diffEngine, dispatcher, transitionDriver);
        var cleanupMiddleware = new CleanupMiddleware(
            diffEngine,
            boundStoreRegistry,
            cleanupService,
            new Nuplane.Store.Cleanup.CleanupPolicyOptions(),
            provider.GetRequiredService<ReconciliationMetrics>(),
            transitionDriver);

        await diffMiddleware.InvokeAsync(context, () => transactionMiddleware.InvokeAsync(context,
            () => cleanupMiddleware.InvokeAsync(context, () => Task.CompletedTask)));

        Assert.Contains("Shared.Dependency", context.ChangeSet!.Removed);
        Assert.True(context.CoordinatedTransitionPreflightPassed);
        Assert.Equal(1, dispatcher.ChangingCount);
        Assert.Equal("2.0.0", changedRoot.Version);
        Assert.Equal(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root.First"] = "1.0.0",
            ["Shared.Dependency"] = "2.1.0",
            ["Independent.Root"] = "1.0.0"
        }, context.MergedActive);

        var state = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(context.MergedActive, state.ActiveVersionById);
        Assert.Equal("LockFileGate", state.LastFailureById["New.Dependency"].Stage);
        Assert.Contains("Root.First", context.ApplyResult!.FailedPackageIds);
        Assert.Contains("New.Dependency", context.ApplyResult.FailedPackageIds);
        Assert.Equal(independentSelection.Graph.GraphId,
            Assert.Single(context.ApplyResult.SuccessfulGraphSelections).Graph.GraphId);
        Assert.Equal("2.1.0", state.ActivePackageDescriptorsByIdNormalized["Shared.Dependency"].Version);
        var retainedGraphs = state.ProtectionRecord!.ActiveClosure.Graphs!;
        var retainedPrior = Assert.Single(retainedGraphs, graph =>
            graph.Nodes.Any(node => node.Install.PackageId == "Root.First"));
        Assert.True(priorSnapshot.HasSamePayloadAs(retainedPrior));
        Assert.Contains(retainedPrior.Nodes, node =>
            node.Install.PackageId == "Shared.Dependency" && node.Install.Version == "2.1.0");
        var activatedIndependent = Assert.Single(retainedGraphs, graph =>
            graph.Nodes.Any(node => node.Install.PackageId == "Independent.Root"));
        Assert.Equal(independentSelection.Graph.GraphId, activatedIndependent.GraphId);
        Assert.Equal(independentSelection.Graph.GenerationId, activatedIndependent.GenerationId);
        Assert.Equal(context.MergedActive!.Count, cleanupService.LastPackageVersions.Count);
        Assert.Equal(context.MergedActive.OrderBy(static item => item.Key), cleanupService.LastPackageVersions
            .Select(static item => new KeyValuePair<string, string>(item.PackageId, item.Version))
            .OrderBy(static item => item.Key));

        static ResolvedPackageGraphSelection CreateSelection(
            PackageRequest rootRequest,
            params ResolvedPackage[] packages)
        {
            var nodes = packages.Select(package => new ResolvedPackageNode(
                package.Id,
                package.Version,
                string.Equals(package.Id, rootRequest.Id, StringComparison.OrdinalIgnoreCase)
                    ? PackageNodeRole.Root
                    : PackageNodeRole.Dependency,
                package.InstallPath,
                PackageSourceKind.RemoteFeed,
                package.SourceName,
                package.PackageContentHash,
                [], [], [])).ToArray();
            var rootNode = nodes.Single(node =>
                string.Equals(node.PackageId, rootRequest.Id, StringComparison.OrdinalIgnoreCase));
            var edges = nodes.Where(node => !ReferenceEquals(node, rootNode)).Select(node => new DependencyEdge(
                rootNode.PackageId,
                rootNode.Version,
                node.PackageId,
                "[1.0.0]",
                node.Version,
                string.Empty,
                Optional: false)).ToArray();
            var roots = new[] { rootNode };
            var graphId = ResolvedPackageGraph.CreateGraphId("net10.0", roots, nodes, edges, []);
            var graph = new ResolvedPackageGraph(graphId, Guid.NewGuid().ToString("N"), "net10.0",
                roots, nodes, edges, [], DateTimeOffset.UnixEpoch);
            return new ResolvedPackageGraphSelection(graph, [rootRequest], packages);
        }
    }

    [Fact]
    public async Task AddNuplane_ApplyFailedChangedDependencyClosureRetainsExactPriorGraph()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var priorState = fixture.States["first"];
        var priorActiveClosure = priorState.ProtectionRecord!.ActiveClosure;
        var priorRecoverableClosure = priorState.ProtectionRecord.RecoverableClosure;
        var priorSnapshot = Assert.Single(priorActiveClosure.Graphs!);
        var updatedRootPath = fixture.Install("Root.First", "2.0.0", "New.Dependency");
        var changedDependencyPath = fixture.Install("New.Dependency", "2.0.0", dependencyId: null);
        var actualHash = "sha512:" + Convert.ToBase64String(new byte[64]);
        var request = new PackageRequest("Root.First", "[2.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "apply-failed-root");
        var packages = new[]
        {
            new ResolvedPackage("Root.First", "2.0.0", "test-feed", updatedRootPath,
                DateTimeOffset.UnixEpoch, "apply-failed-root") { PackageContentHash = actualHash },
            new ResolvedPackage("New.Dependency", "2.0.0", "test-feed", changedDependencyPath,
                DateTimeOffset.UnixEpoch, "dependency-of:Root.First") { PackageContentHash = actualHash }
        };
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new RootRequestSource(request, updatedRootPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher,
            resolverOverride: new MapScopedResolver(packages),
            lockFileCoordinatorOverride: new HashMismatchLockFileCoordinator("New.Dependency", actualHash));

        await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("apply-failed-retention"), CancellationToken.None);

        var state = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(priorState.ActiveVersionById, state.ActiveVersionById);
        Assert.True(priorActiveClosure.HasSamePayloadAs(state.ProtectionRecord!.ActiveClosure));
        Assert.True(priorRecoverableClosure.HasSamePayloadAs(state.ProtectionRecord.RecoverableClosure));
        Assert.Equal(priorSnapshot.GraphId,
            Assert.Single(state.ProtectionRecord.ActiveClosure.Graphs!).GraphId);
        Assert.Equal(priorState.ActivePackageDescriptorsByIdNormalized.Keys.Order(StringComparer.OrdinalIgnoreCase),
            state.ActivePackageDescriptorsByIdNormalized.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (packageId, priorDescriptor) in priorState.ActivePackageDescriptorsByIdNormalized)
        {
            var descriptor = state.ActivePackageDescriptorsByIdNormalized[packageId];
            Assert.Equal(priorDescriptor.Version, descriptor.Version);
            Assert.Equal(priorDescriptor.InstallPath, descriptor.InstallPath);
            Assert.Equal(priorDescriptor.GraphId, descriptor.GraphId);
            Assert.Equal(priorDescriptor.GraphGenerationId, descriptor.GraphGenerationId);
        }
        Assert.Equal("LockFileGate", state.LastFailureById["New.Dependency"].Stage);
        Assert.Equal("apply-failed-retention", state.LastFailureById["New.Dependency"].CorrelationId);
        Assert.Equal(1, dispatcher.ChangingCount);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("path")]
    public async Task AddNuplane_MismatchedResolvedProvenanceIsRefusedBeforeChanging(string mismatch)
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var freshRootPath = fixture.Install("Fresh.Root", "1.0.0", dependencyId: null);
        var request = new PackageRequest("Fresh.Root", "[1.0.0]", "test-feed",
            PackageUpdatePolicy.Exact, "fresh-root");
        var package = new ResolvedPackage("Fresh.Root", "1.0.0", "test-feed", freshRootPath,
            DateTimeOffset.UnixEpoch, "fresh-root");
        var transformed = mismatch switch
        {
            "source" => package with { SourceName = "unselected-source" },
            "path" => package with { InstallPath = Path.Combine(freshRootPath, "unobserved") },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new RootRequestSource(request, freshRootPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher, resolverOverride: new MapScopedResolver([package]),
            lockFileCoordinatorOverride: new TransformingLockFileCoordinator(transformed));

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("invalid-" + mismatch), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.Equal(0, dispatcher.ChangingCount);
        var after = await fixture.ReadFreshStateAsync("first");
        Assert.Equal(fixture.States["first"].ActiveVersionById, after.ActiveVersionById);
        Assert.True(fixture.States["first"].ProtectionRecord!.ActiveClosure
            .HasSamePayloadAs(after.ProtectionRecord!.ActiveClosure));
    }

    [Fact]
    public async Task AddNuplane_RemovalPublishesEmptyStateAndFollowingQuietCycleStaysQuiet()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"],
            new MutableRootRequestSource(fixture.SharedInstallPath), preserveActiveDiff: false,
            dispatcherOverride: dispatcher);
        var service = provider.GetRequiredService<IReconciliationService>();

        await service.TriggerAsync(ReconciliationTrigger.Manual("remove-all"), CancellationToken.None);
        var removed = await fixture.ReadFreshStateAsync("first");
        Assert.Empty(removed.ActiveVersionById);
        Assert.Empty(removed.ProtectionRecord!.ActiveClosure.Graphs!);
        Assert.Equal(1, dispatcher.ChangingCount);

        await service.TriggerAsync(ReconciliationTrigger.Manual("quiet-after-removal"), CancellationToken.None);
        var quiet = await fixture.ReadFreshStateAsync("first");
        Assert.Empty(quiet.ActiveVersionById);
        Assert.True(quiet.ProtectionRecord!.Revision >= removed.ProtectionRecord.Revision);
        Assert.True(removed.ProtectionRecord.ActiveClosure.HasSamePayloadAs(quiet.ProtectionRecord.ActiveClosure));
        Assert.Equal(1, dispatcher.ChangingCount);
    }

    [Theory]
    [InlineData(UnknownParticipant.DiffEngine)]
    [InlineData(UnknownParticipant.DryRunPlanner)]
    [InlineData(UnknownParticipant.DryRunPlannerWithUnknownNestedDiff)]
    [InlineData(UnknownParticipant.LockFileCoordinator)]
    [InlineData(UnknownParticipant.RetryPolicy)]
    public async Task UnknownPathConsumingParticipant_IsRefusedBeforeSourceCallback(UnknownParticipant participant)
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new ScopedFileSource(fixture.SharedInstallPath, entered: null, release: null,
            failAfterRead: false);
        var diffEngine = participant == UnknownParticipant.DiffEngine
            ? Substitute.For<IDesiredActualDiffEngine>()
            : null;
        var dryRunPlanner = participant switch
        {
            UnknownParticipant.DryRunPlanner => Substitute.For<IDryRunPlanner>(),
            UnknownParticipant.DryRunPlannerWithUnknownNestedDiff => new DryRunPlanner(
                Substitute.For<IDesiredActualDiffEngine>()),
            _ => null
        };
        var lockFileCoordinator = participant == UnknownParticipant.LockFileCoordinator
            ? Substitute.For<ILockFileCoordinator>()
            : null;
        var retryPolicy = participant == UnknownParticipant.RetryPolicy
            ? Substitute.For<IReconciliationRetryPolicy>()
            : null;
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            diffEngineOverride: diffEngine, dryRunPlannerOverride: dryRunPlanner,
            lockFileCoordinatorOverride: lockFileCoordinator, retryPolicyOverride: retryPolicy);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                ReconciliationTrigger.Manual("unknown-path-consumer"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);
    }

    [Fact]
    public async Task ScopedResolverReturningInstall_GraphReadFailureIsRecordedWithoutActiveTransition()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var source = new SinglePackageSource("Root.First", fixture.SharedInstallPath);
        var resolver = new ControlledScopedResolver(fixture.SharedInstallPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.SharedInstallPath, "Shared.Dependency.nuspec"),
            "<package><metadata>");
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            resolverOverride: resolver);

        var result = await provider.GetRequiredService<IReconciliationService>().TriggerAsync(
            ReconciliationTrigger.Manual("invalid-graph-metadata"), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);
        Assert.Equal("Root.First", Assert.Single(result.FailedPackages));
        Assert.Equal(1, resolver.CallbackCount);
        var after = await fixture.ReadFreshStateAsync("first");
        var failure = Assert.Single(after.LastFailureById);
        Assert.Equal("Root.First", failure.Key);
        Assert.Equal("resolve", failure.Value.Stage);
        Assert.Equal("invalid-graph-metadata", failure.Value.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(failure.Value.Message));
        Assert.Equal(fixture.States["first"].ActiveVersionById, after.ActiveVersionById);
    }

    [Fact]
    public async Task EnrolledNonemptyTransition_IsRefusedBeforeChangingObserversOrTransactionArtifacts()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var candidatePath = Path.Combine(fixture.PackageInstallRoot, "Candidate.Root", "1.0.0");
        Directory.CreateDirectory(candidatePath);
        var source = new SinglePackageSource("Candidate.Root", candidatePath);
        // A path-free synthetic result reaches preflight without invoking a package reader. The
        // separate real-nuspec test covers the earlier production graph boundary.
        var resolver = new ControlledScopedResolver(candidatePath, returnInstallPath: false);
        var dispatcher = new CountingScopedDispatcher();
        using var provider = CreateProvider(fixture.PackageInstallRoot, fixture.StatePaths["first"], source,
            preserveActiveDiff: false, dispatcherOverride: dispatcher, resolverOverride: resolver);
        var entriesBefore = Directory.GetFileSystemEntries(fixture.PackageInstallRoot)
            .Order(StringComparer.Ordinal).ToArray();

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                ReconciliationTrigger.Manual("nonempty-enrolled"), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, error.Reason);
        Assert.Equal(1, source.CallbackCount);
        Assert.Equal(1, resolver.CallbackCount);
        Assert.Equal(0, dispatcher.ChangingCount);
        Assert.Equal(0, dispatcher.ChangedCount);
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(fixture.PackageInstallRoot)
            .Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ManualCustomStoreComposition_PreservesOnlyPositivelyUnenrolledRootBehavior()
    {
        using var temp = new TempDirectory();
        var unenrolledRoot = temp.CreateSubdirectory("unenrolled-packages");
        var customRegistry = Substitute.For<IStoreRegistry>();
        var admission = PackageStoreRuntimeAdmission.CreateManual(customRegistry,
            new FeedResolutionOptions { PackageInstallRoot = unenrolledRoot });

        await using (var positive = await admission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation, CancellationToken.None))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, positive.Status);
            Assert.Null(positive.Owner);
        }

        var customSerializer = Substitute.For<IStoreStateSerializer>();
        var customSerializerRegistry = new StoreRegistry(customSerializer, stateFilePath: null);
        var serializerAdmission = PackageStoreRuntimeAdmission.CreateManual(customSerializerRegistry,
            new FeedResolutionOptions { PackageInstallRoot = unenrolledRoot });
        await using (var serializerPositive = await serializerAdmission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation, CancellationToken.None))
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, serializerPositive.Status);

        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var protectedAdmission = PackageStoreRuntimeAdmission.CreateManual(customRegistry,
            new FeedResolutionOptions { PackageInstallRoot = fixture.PackageInstallRoot });
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await protectedAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());

        var protectedSerializerAdmission = PackageStoreRuntimeAdmission.CreateManual(customSerializerRegistry,
            new FeedResolutionOptions { PackageInstallRoot = fixture.PackageInstallRoot });
        var serializerRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await protectedSerializerAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, serializerRefusal.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddNuplaneCustomRegistry_PreservesUnenrolledExistingAndMissingRootBehavior(bool missingRoot)
    {
        using var temp = new TempDirectory();
        var root = missingRoot
            ? Path.Combine(temp.Path, "absent", "nested-packages")
            : temp.CreateSubdirectory("existing-packages");
        var customRegistry = Substitute.For<IStoreRegistry>();
        var observingSerializer = new ObservingProtectionSerializer();
        using var provider = CreateAdmissionProvider(root, customRegistry, observingSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, operation.Status);
        Assert.Null(operation.Owner);
        Assert.Equal(missingRoot, operation.Root is null);
        Assert.Equal(0, observingSerializer.PayloadReadCount);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddNuplaneUnsupportedSerializer_PreservesUnenrolledExistingAndMissingRootBehavior(bool missingRoot)
    {
        using var temp = new TempDirectory();
        var root = missingRoot
            ? Path.Combine(temp.Path, "absent", "nested-packages")
            : temp.CreateSubdirectory("existing-packages");
        var unsupportedSerializer = Substitute.For<IStoreStateSerializer>();
        using var provider = CreateAdmissionProvider(root, registryOverride: null, unsupportedSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        await using var operation = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, operation.Status);
        Assert.Null(operation.Owner);
        Assert.Equal(missingRoot, operation.Root is null);
        await unsupportedSerializer.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await unsupportedSerializer.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNuplaneCustomRegistry_RefusesCompleteRootBeforeSelectedStatePayloadOrRegistryRead()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var customRegistry = Substitute.For<IStoreRegistry>();
        var observingSerializer = new ObservingProtectionSerializer();
        using var provider = CreateAdmissionProvider(fixture.PackageInstallRoot, customRegistry, observingSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, observingSerializer.PayloadReadCount);
        await customRegistry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddNuplaneUnsupportedSerializer_RefusesCompleteRootBeforeSelectedStatePayloadRead()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var unsupportedSerializer = Substitute.For<IStoreStateSerializer>();
        using var provider = CreateAdmissionProvider(fixture.PackageInstallRoot, registryOverride: null,
            unsupportedSerializer);

        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var ignored = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        await unsupportedSerializer.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await unsupportedSerializer.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicManualLkg_RefusesProtectedInstallPathBeforeValidationOrObservers()
    {
        using var fixture = await CompletedMembershipFixture.CreateAsync();
        var selectedRegistry = Substitute.For<IStoreRegistry>();
        selectedRegistry.GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fixture.States["first"]));
        var dispatcher = Substitute.For<IObserverEventDispatcher>();
        var recovery = new LastKnownGoodStartupRecoveryService(selectedRegistry, dispatcher,
            new StartupRecoveryState());

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            recovery.TryRecoverAsync("manual-protected-lkg", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(fixture.RootIdentity, error.Root);
        await selectedRegistry.Received(1).GetStateAsync(Arg.Any<CancellationToken>());
        Assert.Empty(dispatcher.ReceivedCalls());
    }

    private static ServiceProvider CreateProvider(
        string packageInstallRoot,
        string stateFilePath,
        IDesiredPackageSource source,
        bool preserveActiveDiff = true,
        IObserverEventDispatcher? dispatcherOverride = null,
        IPackageResolver? resolverOverride = null,
        IDesiredActualDiffEngine? diffEngineOverride = null,
        IDryRunPlanner? dryRunPlannerOverride = null,
        ILockFileCoordinator? lockFileCoordinatorOverride = null,
        IReconciliationRetryPolicy? retryPolicyOverride = null,
        LockFileMode? lockFileMode = null,
        IDesiredPackageSource? additionalSource = null,
        string? additionalPackageStoreRoot = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder =>
        {
            if (additionalPackageStoreRoot is not null)
                builder.AddPackageStoreRoot("peer", additionalPackageStoreRoot);
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = packageInstallRoot);
        services.Configure<StoreRegistryOptions>(options => options.StateFilePath = stateFilePath);
        services.Configure<ReconciliationOptions>(options => options.MaxRetryAttempts = 0);
        if (lockFileMode is { } selectedLockFileMode)
            services.Configure<LockFileOptions>(options => options.Mode = selectedLockFileMode);

        services.RemoveAll<IDesiredPackageSource>();
        services.AddSingleton<IDesiredPackageSource>(source);
        if (additionalSource is not null)
            services.AddSingleton<IDesiredPackageSource>(additionalSource);
        services.RemoveAll<IDesiredStateContributor>();
        services.RemoveAll<IPackageResolver>();
        services.AddSingleton<IPackageResolver>(resolverOverride ?? (IPackageResolver)new NeverCalledScopedResolver());
        services.RemoveAll<IDesiredActualDiffEngine>();
        if (diffEngineOverride is not null)
            services.AddSingleton<IDesiredActualDiffEngine>(diffEngineOverride);
        else if (preserveActiveDiff)
            services.AddSingleton<IDesiredActualDiffEngine, PreserveActiveDiffEngine>();
        else
            services.AddSingleton<IDesiredActualDiffEngine, DesiredActualDiffEngine>();
        if (dryRunPlannerOverride is not null)
        {
            services.RemoveAll<IDryRunPlanner>();
            services.AddSingleton<IDryRunPlanner>(dryRunPlannerOverride);
        }
        if (lockFileCoordinatorOverride is not null)
        {
            services.RemoveAll<ILockFileCoordinator>();
            services.AddSingleton<ILockFileCoordinator>(lockFileCoordinatorOverride);
        }
        if (retryPolicyOverride is not null)
        {
            services.RemoveAll<IReconciliationRetryPolicy>();
            services.AddSingleton<IReconciliationRetryPolicy>(retryPolicyOverride);
        }
        if (dispatcherOverride is not null)
        {
            services.RemoveAll<IObserverEventDispatcher>();
            services.AddSingleton<IObserverEventDispatcher>(dispatcherOverride);
        }
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateAdmissionProvider(
        string packageInstallRoot,
        IStoreRegistry? registryOverride,
        IStoreStateSerializer serializerOverride)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = packageInstallRoot);
        services.RemoveAll<IStoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(serializerOverride);
        if (registryOverride is not null)
        {
            services.RemoveAll<IStoreRegistry>();
            services.AddSingleton<IStoreRegistry>(registryOverride);
        }
        return services.BuildServiceProvider();
    }

    private sealed class ObservingProtectionSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private int _payloadReadCount;

        internal int PayloadReadCount => Volatile.Read(ref _payloadReadCount);

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _payloadReadCount);
            return await _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class ScopedFileSource(
        string installDirectory,
        TaskCompletionSource<string>? entered,
        Task? release,
        bool failAfterRead) : IScopedDesiredPackageSource
    {
        private int _callbackCount;
        private int _sentinelReadCount;

        internal int CallbackCount => Volatile.Read(ref _callbackCount);
        internal int SentinelReadCount => Volatile.Read(ref _sentinelReadCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installDirectory);
            var text = await File.ReadAllTextAsync(Path.Combine(installDirectory, "sentinel.txt"), ct);
            Interlocked.Increment(ref _sentinelReadCount);
            entered?.TrySetResult(text);
            if (release is not null)
                await release.WaitAsync(ct);
            if (failAfterRead)
                throw new IOException("controlled desired-source failure after the scoped read");
            return Array.Empty<PackageRequest>();
        }
    }

    private sealed class UnscopedSourceProbe : IDesiredPackageSource
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _callbackCount);
            return Task.FromResult<IReadOnlyList<PackageRequest>>(Array.Empty<PackageRequest>());
        }
    }

    private sealed class NeverCalledScopedResolver : IScopedPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException("The empty source should not resolve packages."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException("The empty source should not resolve packages."));
    }

    private sealed class PreserveActiveDiffEngine : IPackagePathIndependentDesiredActualDiffEngine
    {
        public PackageChangeSet Compute(IReadOnlyCollection<ResolvedPackage> desired,
            IReadOnlyDictionary<string, string> activeVersions, string correlationId, DateTimeOffset timestamp)
            => new([], [], [], correlationId, timestamp);

        public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired)
            => desired.ToDictionary(static package => package.Id, static package => package.Version,
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SinglePackageSource(string packageId, string installPath) : IScopedDesiredPackageSource
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installPath);
            IReadOnlyList<PackageRequest> requests =
            [new PackageRequest(packageId, "1.0.0", "test-feed", PackageUpdatePolicy.Exact, "nonempty-source")];
            return Task.FromResult(requests);
        }
    }

    private sealed class ControlledScopedResolver(string installPath, bool returnInstallPath = true) : IScopedPackageResolver
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException(
                "An enrolled resolver must be invoked through its scoped overload."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            borrow.ValidateForInstallPath(installPath);
            return Task.FromResult(new ResolvedPackage(request.Id, "1.0.0", "test-feed", returnInstallPath ? installPath : string.Empty,
                DateTimeOffset.UnixEpoch, request.SourceName)
            {
                PackageContentHash = "sha512:" + Convert.ToBase64String(new byte[64])
            });
        }
    }

    private sealed class RootRequestSource(PackageRequest request, string installPath) : IScopedDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            borrow.ValidateForInstallPath(installPath);
            return Task.FromResult<IReadOnlyList<PackageRequest>>([request]);
        }
    }

    private sealed class MutableRootRequestSource(string installPath) : IScopedDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<PackageRequest>>(new InvalidOperationException(
                "An enrolled source must be invoked through its scoped overload."));

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            borrow.ValidateForInstallPath(installPath);
            return Task.FromResult<IReadOnlyList<PackageRequest>>([]);
        }
    }

    private sealed class MapScopedResolver(IEnumerable<ResolvedPackage> packages) : IScopedPackageResolver
    {
        private readonly IReadOnlyDictionary<string, ResolvedPackage> _packages = packages
            .ToDictionary(static package => package.Id, StringComparer.OrdinalIgnoreCase);

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
            => Task.FromException<ResolvedPackage>(new InvalidOperationException(
                "An enrolled resolver must be invoked through its scoped overload."));

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            if (!_packages.TryGetValue(request.Id, out var package))
                return Task.FromException<ResolvedPackage>(new InvalidOperationException(
                    $"No owned fixture package was registered for '{request.Id}'."));
            borrow.ValidateForInstallPath(package.InstallPath);
            return Task.FromResult(package);
        }
    }

    private sealed class RejectRootLockFileCoordinator(string rejectedPackageId)
        : IPackagePathIndependentLockFileCoordinator
    {
        public Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved,
            CancellationToken cancellationToken)
            => Task.FromResult(StringComparer.OrdinalIgnoreCase.Equals(resolved.Id, rejectedPackageId)
                ? new LockFileEvaluationResult(false, "fixture-rejected", null, null)
                : new LockFileEvaluationResult(true, "fixture-allowed", resolved, null));
    }

    private sealed class TransformingLockFileCoordinator(ResolvedPackage transformedPackage)
        : IPackagePathIndependentLockFileCoordinator
    {
        public Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved,
            CancellationToken cancellationToken)
            => Task.FromResult(new LockFileEvaluationResult(true, "fixture-transformed",
                StringComparer.OrdinalIgnoreCase.Equals(resolved.Id, transformedPackage.Id)
                    ? transformedPackage
                    : resolved,
                null));
    }

    private sealed class HashMismatchLockFileCoordinator(string failedPackageId, string actualHash)
        : IPackagePathIndependentLockFileCoordinator, ILockFileCycleCoordinator
    {
        private readonly string _mismatchedHash = "sha512:" + Convert.ToBase64String(
            Enumerable.Repeat((byte)1, 64).ToArray());

        public Task<LockFileEvaluationResult> EvaluateAsync(
            ResolvedPackage resolved,
            CancellationToken cancellationToken)
            => Task.FromResult(EvaluatePackage(resolved));

        public Task<LockFileSnapshot> CaptureAsync(CancellationToken cancellationToken)
            => Task.FromResult(new LockFileSnapshot(LockFileMode.Enforce, null,
                new Dictionary<string, PackageLockEntry>(StringComparer.OrdinalIgnoreCase), null));

        public PackageRequest ConstrainRequest(LockFileSnapshot snapshot, PackageRequest request) => request;

        public LockFileEvaluationResult Evaluate(LockFileSnapshot snapshot, ResolvedPackage resolved)
            => EvaluatePackage(resolved);

        public Task GenerateAsync(
            LockFileSnapshot snapshot,
            IReadOnlyList<ResolvedPackage> resolvedPackages,
            DateTimeOffset generatedAt,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        private LockFileEvaluationResult EvaluatePackage(ResolvedPackage resolved)
            => new(true, "controlled-apply-hash", resolved,
                string.Equals(resolved.Id, failedPackageId, StringComparison.OrdinalIgnoreCase)
                    ? _mismatchedHash
                    : actualHash);
    }

    private sealed class CountingScopedDispatcher : IScopedObserverEventDispatcher
    {
        private int _changingCount;
        private int _changedCount;

        internal int ChangingCount => Volatile.Read(ref _changingCount);
        internal int ChangedCount => Volatile.Read(ref _changedCount);

        public void ValidateCoordinatedParticipants() { }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changingCount);
            return Task.CompletedTask;
        }

        public Task PublishChangingAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changingCount);
            return Task.CompletedTask;
        }

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changedCount);
            return Task.CompletedTask;
        }

        public Task PublishChangedAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _changedCount);
            return Task.CompletedTask;
        }

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
            PackageStoreOperationOwner owner, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PublishReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, PackageStoreOperationOwner owner,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingPackageCleanupService : Nuplane.Store.Cleanup.IPackageCleanupService
    {
        internal IReadOnlyList<Nuplane.Store.Cleanup.PackageVersionEntry> LastPackageVersions { get; private set; } = [];

        public Task<IReadOnlyList<CleanupDecision>> ExecuteAutomaticAsync(
            IReadOnlyList<Nuplane.Store.Cleanup.PackageVersionEntry> packageVersions,
            Nuplane.Store.Cleanup.CleanupPolicyOptions options,
            string correlationId,
            bool triggerOnSuccessfulReconciliation,
            CancellationToken cancellationToken)
        {
            LastPackageVersions = packageVersions.ToArray();
            return Task.FromResult<IReadOnlyList<CleanupDecision>>([]);
        }
    }

    private sealed class CompletedMembershipFixture : IDisposable
    {
        internal TempDirectory Temp { get; } = new();
        internal IPhysicalStoreFileSystem Files { get; } = PackageStoreRuntimeAdmission.CreatePhysicalFileSystem();
        internal string PackageInstallRoot { get; private set; } = null!;
        internal string SharedInstallPath { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal Dictionary<string, string> StatePaths { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, StoreStateRecord> States { get; } = new(StringComparer.Ordinal);

        internal static async Task<CompletedMembershipFixture> CreateAsync()
        {
            var fixture = new CompletedMembershipFixture();
            try
            {
                fixture.PackageInstallRoot = fixture.Temp.CreateSubdirectory("packages");
                fixture.StatePaths.Add("first", Path.Combine(fixture.Temp.Path, "state-first", "state.json"));
                fixture.StatePaths.Add("second", Path.Combine(fixture.Temp.Path, "state-second", "state.json"));
                foreach (var path in fixture.StatePaths.Values)
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var root = OpenDirectory(fixture.Files, fixture.PackageInstallRoot);
                fixture.RootIdentity = new PhysicalRootIdentity(fixture.Files.InspectHandle(root).Identity);
                var serializer = new StoreStateSerializer();
                var registry = new RootMembershipRegistry(fixture.Files, serializer);
                var declarations = fixture.StatePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
                    new RootMemberRecord.DeclaredBinding())).ToArray();
                // Migration inputs are read while positively Unenrolled, before declaring authority.
                fixture.SharedInstallPath = fixture.Install("Shared.Dependency", "2.1.0", dependencyId: null);
                fixture.States.Add("first", await fixture.BuildProtectedStateAsync("first", "Root.First"));
                fixture.States.Add("second", await fixture.BuildProtectedStateAsync("second", "Root.Second"));
                registry.InitializeIncomplete(root, fixture.RootIdentity, 1, declarations, true, CancellationToken.None);

                var parents = fixture.StatePaths.ToDictionary(pair => pair.Key,
                    pair => OpenDirectory(fixture.Files, Path.GetDirectoryName(pair.Value)!), StringComparer.Ordinal);
                try
                {
                    await registry.BindDeclaredMembersAsync(root, fixture.RootIdentity, 1, declarations,
                        parents.ToDictionary(pair => pair.Key,
                            pair => (pair.Value, Path.GetFileName(fixture.StatePaths[pair.Key])), StringComparer.Ordinal),
                        true, CancellationToken.None);
                }
                finally
                {
                    foreach (var parent in parents.Values.Reverse())
                        parent.Dispose();
                }

                await registry.WithQuiescentBoundIncompleteMemberLocationsAsync(root, fixture.RootIdentity, 1, true,
                    async (locked, token) =>
                    {
                        foreach (var member in locked.Ledger.Members)
                            await locked.PublishStateAsync(member.MemberId, fixture.States[member.MemberId], token);
                        return true;
                    }, CancellationToken.None);
                await registry.CompleteEnrollmentAsync(root, fixture.RootIdentity, 1, true, CancellationToken.None);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal async Task<StoreStateRecord> ReadFreshStateAsync(string memberId)
        {
            var registry = new StoreRegistry(new StoreStateSerializer(), StatePaths[memberId]);
            return await registry.GetStateAsync(CancellationToken.None);
        }

        private async Task<StoreStateRecord> BuildProtectedStateAsync(string memberId, string rootPackageId)
        {
            var rootPath = Install(rootPackageId, "1.0.0", "Shared.Dependency");
            var request = new PackageRequest(rootPackageId, string.Empty, "test-feed", PackageUpdatePolicy.Range, memberId);
            var packages = new[]
            {
                new ResolvedPackage(rootPackageId, "1.0.0", "test-feed", rootPath, DateTimeOffset.UnixEpoch, memberId),
                new ResolvedPackage("Shared.Dependency", "2.1.0", "test-feed", SharedInstallPath,
                    DateTimeOffset.UnixEpoch, "dependency")
            };
            var graphResolver = new PackageDependencyGraphResolver(Substitute.For<IPackageResolver>(),
                Substitute.For<IReconciliationRetryPolicy>());
            var resolution = await graphResolver.ResolveAsync([request], (_, _) => Task.FromResult(packages[0]),
                (_, _) => Task.FromResult(packages[1]), CancellationToken.None);
            var graph = Assert.Single(resolution.ResolvedGraphs);
            var installs = new List<PackageInstallIdentity>();
            using var root = OpenDirectory(Files, PackageInstallRoot);
            foreach (var node in graph.Nodes)
            {
                var relativePath = Path.GetRelativePath(PackageInstallRoot, node.InstallPath!).Replace('\\', '/');
                using var observation = new PackageInstallIdentityReader(Files).Observe(
                    root, RootIdentity, relativePath, node.PackageId, node.Version);
                installs.Add(observation.InstallIdentity);
            }

            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(graph, [request], installs, 1);
            var versions = packages.ToDictionary(static package => package.Id, static package => package.Version,
                StringComparer.OrdinalIgnoreCase);
            var empty = StoreStateRecord.Empty();
            var changes = new PackageChangeSet(packages, [], [], memberId, DateTimeOffset.UnixEpoch);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(empty, versions, packages, changes,
                memberId, DateTimeOffset.UnixEpoch, [graph]);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(empty, [graph], versions,
                memberId, DateTimeOffset.UnixEpoch);
            var state = empty with
            {
                ActiveVersionById = versions,
                LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors,
                    StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords,
                    StringComparer.OrdinalIgnoreCase),
                UpdatedAt = DateTimeOffset.UnixEpoch
            };
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            var candidate = new PackageProtectionRecord(1, RootIdentity, 1, memberId, 1,
                ProtectionDigest.StateBody(state), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, RootIdentity, 1, memberId, 1,
                candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);
            return state with { ProtectionRecord = protection };
        }

        internal string Install(string packageId, string version, string? dependencyId)
        {
            var path = Path.Combine(PackageInstallRoot, "test-feed", packageId, version);
            Directory.CreateDirectory(path);
            var dependencies = dependencyId is null ? string.Empty
                : $"<dependencies><dependency id=\"{dependencyId}\" version=\"2.0.0\" /></dependencies>";
            File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
                $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependencies}</metadata></package>");
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
            if (packageId == "Shared.Dependency")
                File.WriteAllText(Path.Combine(path, "sentinel.txt"), "shared sentinel");
            return path;
        }

        public void Dispose() => Temp.Dispose();
    }

    private static PhysicalStoreDirectoryHandle OpenDirectory(IPhysicalStoreFileSystem files, string ownedPath)
    {
        var path = OperatingSystem.IsWindows() ? Path.GetFullPath(ownedPath) : RealPath(ownedPath);
        var anchor = OperatingSystem.IsWindows() ? Path.GetPathRoot(path)! : "/";
        var current = files.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in path[anchor.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = files.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static string RealPath(string path)
    {
        var pointer = OperatingSystem.IsMacOS() ? DarwinRealPath(path, IntPtr.Zero) : UnixRealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            throw new IOException($"Temporary fixture realpath failed ({Marshal.GetLastPInvokeError()}).");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Temporary fixture realpath returned no path."); }
        finally
        {
            if (OperatingSystem.IsMacOS()) DarwinFree(pointer);
            else UnixFree(pointer);
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr DarwinRealPath(string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr UnixRealPath(string path, IntPtr buffer);
    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    private static extern void DarwinFree(IntPtr pointer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void UnixFree(IntPtr pointer);
}
