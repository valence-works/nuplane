using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Registration;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantScopedLoading_UsesContextOwnedLeaseForDeferredAssemblyQueries()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a", participant: true);
        Assert.Equal(PackageStoreCompositionMode.CoordinatedParticipant,
            provider.GetRequiredService<PackageStoreCompositionSnapshot>().Mode);
        Assert.NotNull(provider.GetRequiredService<IReconciliationService>());
        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            var result = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);

            Assert.Empty(result.FailedByPackageId);
            AssertContextLeaseAndDeferredAssemblies(provider, owner.Lease);
        }
        finally
        {
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertContextLeaseAndDeferredAssemblies(IServiceProvider provider, PackageGraphUseLease lease)
    {
        Assert.True(provider.GetRequiredService<PackageLoader>().TryGetContext("Root.First", "1.0.0", out var handle));
        var context = Assert.IsAssignableFrom<IGraphUseLeaseBoundLoadContext>(handle!.Context);
        Assert.Same(lease, context.GraphUseLease);
        Assert.Contains(provider.GetRequiredService<PackageAssemblyProvider>().GetAssemblies("Root.First", "1.0.0"),
            assembly => assembly.GetName().Name == "Root.First");
    }

    [Fact]
    public async Task ScopedAssemblyScan_RefusesDifferentInstallBeforeNativePackageOpen()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a");
        var graph = fixture.Graphs["member-a"];
        await using var owner = await AcquireGraphUseAsync(provider, graph);
        var barrier = fixture.Files.BlockNextPackageFileOpen();
        try
        {
            var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
                provider.GetRequiredService<PackageLoader>().BuildScanCandidates("Root.First",
                    Path.Combine(fixture.RootPath, "outside-the-leased-graph"), owner.Lease));

            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
            Assert.Equal(-1, barrier.RootLockCountAtPause);
        }
        finally
        {
            barrier.Release();
        }
    }
}
