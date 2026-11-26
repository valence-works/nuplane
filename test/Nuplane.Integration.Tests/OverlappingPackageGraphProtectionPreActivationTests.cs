using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadUnchangedStateAsync_WithPendingLease_VerifiesOnlyTheConfiguredMembersRevision(bool mutateConfiguredMember)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var target = fixture.CreateProvider("member-b", participant: true);
        await using var sibling = fixture.CreateProvider("member-a", participant: true);
        var targetAdmission = target.GetRequiredService<IPackageStoreAdmission>();
        var targetRegistry = (ICoordinatedStoreRegistry)target.GetRequiredService<IStoreRegistry>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var next = fixture.Graphs["member-b-v2"];
        var selection = new ResolvedPackageGraphSelection(next.Graph, next.Requests, next.Packages);
        var context = new ReconciliationCycleContext
        {
            CorrelationId = "pending-member-cas",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = deadline.Token,
            DesiredRequests = next.Requests,
            ChangeSet = new PackageChangeSet([], next.Packages, [], "pending-member-cas", DateTimeOffset.UtcNow),
            ApplyResult = new PackageApplyExecutionResult(next.Packages, []) { SuccessfulGraphSelections = [selection] },
        };

        CoordinatedPreActivationCycle plan;
        await using (var operation = await targetAdmission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation, deadline.Token))
        {
            plan = await CoordinatedPreActivationCycle.PrepareAsync(
                operation.Owner!, targetRegistry, target.GetRequiredService<IResolvedPackageGraphUseLeaseAcquisition>(),
                context, deadline.Token);
        }

        await using (plan)
        {
            var pending = Assert.Single((await InspectGraphUseRecordsAsync(targetAdmission, fixture)).Entries);
            Assert.Equal(GraphUseRecordOwnershipState.Live, pending.OwnershipState);
            Assert.Equal(PackageGraphUseSnapshotState.Pending, pending.Record.SnapshotState);
            Assert.Equal(next.Graph.GenerationId, pending.Record.GraphSnapshot.GenerationId);

            var mutator = mutateConfiguredMember ? target : sibling;
            await using (var write = await mutator.GetRequiredService<IPackageStoreAdmission>()
                             .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation, deadline.Token))
            {
                using var borrow = write.Owner!.Borrow();
                var registry = (ICoordinatedStoreRegistry)mutator.GetRequiredService<IStoreRegistry>();
                await registry.PersistCoordinatedFailureAsync(
                    borrow, "Concurrent.Package", "test", "A writer advanced the member.", "concurrent-test", deadline.Token);
            }

            await using var fresh = await targetAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, deadline.Token);
            if (mutateConfiguredMember)
            {
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                    () => plan.ReadUnchangedStateAsync(fresh.Owner!, targetRegistry, deadline.Token));
                Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
            }
            else
            {
                var current = await plan.ReadUnchangedStateAsync(fresh.Owner!, targetRegistry, deadline.Token);
                Assert.Equal("1.0.0", current.ActiveVersionById["Root.Second"]);
                Assert.Equal(plan.PriorState.ProtectionRecord!.Revision, current.ProtectionRecord!.Revision);
            }
        }

        var released = Assert.Single((await InspectGraphUseRecordsAsync(targetAdmission, fixture)).Entries);
        Assert.Equal(GraphUseRecordOwnershipState.Stale, released.OwnershipState);
    }

    [Fact]
    public async Task TriggerAsync_ParticipantLoadRejected_PreservesPriorActiveAndRecoverableClosure()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new PendingActivationGate();
        var observer = new PostLoadingObserverProbe();
        var setup = await CreatePostLoadingUpdateProviderAsync(fixture, observer, activationGate: gate);
        await using var provider = setup.Provider;
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var registry = (ICoordinatedStoreRegistry)provider.GetRequiredService<IStoreRegistry>();
        var prior = await ReadStateAsync(deadline.Token);

        gate.OnEvaluate = async (context, cancellationToken) =>
        {
            var pending = Assert.Single(context.GraphUseLeases);
            Assert.Equal(PackageGraphUseSnapshotState.Pending, pending.Snapshot.State);
            Assert.Contains(context.Packages, package => package.Id == "Root.Second" && package.Version == "3.0.0");

            // The callback can acquire the same native root and read the previous committed graph.
            var whileLoading = await ReadStateAsync(cancellationToken);
            Assert.Equal("1.0.0", whileLoading.ActiveVersionById["Root.Second"]);
            Assert.Equal("1.0.0", whileLoading.LastKnownGoodById["Root.Second"]);
            return PackageActivationGateResult.Block("Staged activation refused by the test gate.");
        };

        try
        {
            var result = await provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                ReconciliationTrigger.Manual("coordinated-staged-activation-failure"), deadline.Token);

            Assert.True(result.IsDegraded);
            Assert.Contains("Root.Second", result.FailedPackages, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(result.ChangeSet.Updated);
            Assert.Equal(1, gate.Invocations);
            Assert.Equal(0, observer.CallbackCount);

            var current = await ReadStateAsync(deadline.Token);
            Assert.Equal("1.0.0", current.ActiveVersionById["Root.Second"]);
            Assert.Equal("1.0.0", current.LastKnownGoodById["Root.Second"]);
            Assert.Equal("2.1.0", current.ActiveVersionById["Shared.Dependency"]);
            Assert.Equal("load", current.LastFailureById["Root.Second"].Stage);
            Assert.Contains("Staged activation refused", current.LastFailureById["Root.Second"].Message);
            Assert.Equal(Assert.Single(prior.ActiveGraphsByIdNormalized.Values).GenerationId,
                Assert.Single(current.ActiveGraphsByIdNormalized.Values).GenerationId);
            Assert.Equal(Assert.Single(prior.ProtectionRecord!.RecoverableClosure.Graphs!).SnapshotId,
                Assert.Single(current.ProtectionRecord!.RecoverableClosure.Graphs!).SnapshotId);
        }
        finally
        {
            await ReleasePostLoadingGraphAsync(provider, fixture);
            Directory.Delete(setup.FeedPath, recursive: true);
        }

        async Task<StoreStateRecord> ReadStateAsync(CancellationToken cancellationToken)
        {
            await using var operation = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation, cancellationToken);
            using var borrow = operation.Owner!.Borrow();
            return await registry.ReadCoordinatedStateAsync(borrow, cancellationToken);
        }
    }

    private sealed class PendingActivationGate : IScopedPackageActivationGate
    {
        internal int Invocations { get; private set; }
        internal Func<PackageActivationContext, CancellationToken, ValueTask<PackageActivationGateResult>> OnEvaluate { get; set; } =
            static (_, _) => ValueTask.FromResult(PackageActivationGateResult.Allow);

        public ValueTask<PackageActivationGateResult> EvaluateAsync(PackageActivationContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return OnEvaluate(context, cancellationToken);
        }
    }
}
