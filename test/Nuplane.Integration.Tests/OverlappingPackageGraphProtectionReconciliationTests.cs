using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task TwoReconciliationProviders_RetainLoadingAcrossAnotherCommittedGeneration()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var barrier = new ReconciliationLoadingBarrier();
        await using var providerA = fixture.CreateProvider("member-a", activationGate: barrier,
            desiredRequests: fixture.Graphs["member-a"].Requests);
        await using var providerB = fixture.CreateProvider("member-b", desiredRequests: fixture.Graphs["member-b-v2"].Requests);
        Assert.IsType<MultiFeedPackageResolver>(providerA.GetRequiredService<IPackageResolver>());
        Assert.IsType<NuGetRemotePackageAcquirer>(providerA.GetRequiredService<IRemotePackageAcquirer>());

        var cycleA = Task.Run(() => providerA.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("automatic-retained-a"), CancellationToken.None));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // A is inside the real Loading observer, after its resolve/apply/publication phase.
            // B must acquire the same root and both members while A's graph remains protected.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await providerB.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("automatic-new-b"), deadline.Token);
            var stateB = await providerB.GetRequiredService<IStoreRegistry>().GetStateAsync(CancellationToken.None);
            Assert.Equal("2.0.0", stateB.ActiveVersionById["Root.Second"]);
            var committedGraphB = Assert.Single(PersistedStoreStateGraphVerifier.Verify(stateB).ActiveGraphs);
            Assert.NotEqual(fixture.Graphs["member-b"].Graph.GenerationId, committedGraphB.GenerationId);
            Assert.Equal("Root.Second", Assert.Single(committedGraphB.RequestedRoots).Request.Id);
            AssertGraphLoaded(providerB, fixture.Graphs["member-b-v2"], "Root.Second", "Shared.Dependency");

            var uses = await InspectGraphUseRecordsAsync(providerB.GetRequiredService<IPackageStoreAdmission>(), fixture);
            Assert.Equal(2, uses.Entries.Count);
            Assert.All(uses.Entries, entry => Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState));
            Assert.False(cycleA.IsCompleted);

            barrier.Release();
            await cycleA.WaitAsync(TimeSpan.FromSeconds(30));
            var stateA = await providerA.GetRequiredService<IStoreRegistry>().GetStateAsync(CancellationToken.None);
            Assert.Equal("1.0.0", stateA.ActiveVersionById["Root.First"]);
            AssertGraphLoaded(providerA, fixture.Graphs["member-a"], "Root.First", "Shared.Dependency");
        }
        finally
        {
            barrier.Release();
            try { await cycleA; }
            finally
            {
                providerA.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                providerB.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                await fixture.WaitForGraphUseReleaseAsync(providerA.GetRequiredService<IPackageStoreAdmission>());
            }
        }
    }

    private sealed class ReconciliationLoadingBarrier : IPackagePathIndependentActivationGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public async ValueTask<PackageActivationGateResult> EvaluateAsync(PackageActivationContext context,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return PackageActivationGateResult.Allow;
        }
    }
}
