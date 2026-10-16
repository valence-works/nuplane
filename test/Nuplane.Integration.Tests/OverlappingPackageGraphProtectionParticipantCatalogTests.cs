using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantCatalogProjection_UsesLoadedContextLeaseWithoutLoadingAssemblies()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a", participant: true);
        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);

            var assembliesBeforeProjection = GetLoadedContextAssemblyNames(provider, owner);

            provider.GetRequiredService<LoadingCatalogRefreshTracker>().MarkRefreshed("participant-catalog-projection");
            var snapshot = await provider.GetRequiredService<IPackageLoadStateCatalog>()
                .GetLoadStateAsync(CancellationToken.None);

            Assert.Equal(PackageLoadStateAvailability.Available, snapshot.Availability);
            Assert.Contains(snapshot.Packages, state => state.PackageId == "Root.First" &&
                state.Status == PackageLoadStatus.Loaded && state.AssemblyReferences.Count > 0);
            Assert.Contains(snapshot.Packages, state => state.PackageId == "Shared.Dependency" &&
                state.Status == PackageLoadStatus.Loaded && state.AssemblyReferences.Count > 0);
            Assert.Equal(assembliesBeforeProjection, GetLoadedContextAssemblyNames(provider, owner));
        }
        finally
        {
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [Fact]
    public async Task ParticipantCatalogProjection_RefusesSessionPathMismatchBeforeScanning()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = fixture.CreateProvider("member-a", participant: true);
        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            var load = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
                [graph.CreateEnvelope(owner)], [], CancellationToken.None);
            Assert.Empty(load.FailedByPackageId);
            var loader = provider.GetRequiredService<PackageLoader>();
            var mismatchedPackage = new ActivePackage("Root.First", "1.0.0", "feed", "member-a",
                Path.Combine(fixture.RootPath, "outside-the-leased-graph"), DateTimeOffset.UnixEpoch,
                "participant-catalog-mismatch");

            var refusal = Assert.Throws<PackageStoreAdmissionException>(() => PackageLoadStateProjection.Project(
                mismatchedPackage, loader, new AssemblyScanCandidateProjector(loader)));

            Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        }
        finally
        {
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] GetLoadedContextAssemblyNames(
        ServiceProvider provider,
        PackageGraphUseLeaseOwner owner)
    {
        Assert.True(provider.GetRequiredService<PackageLoader>()
            .TryGetContext("Root.First", "1.0.0", out var handle));
        var context = Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
        var leaseBoundContext = Assert.IsAssignableFrom<IGraphUseLeaseBoundLoadContext>(context);
        Assert.Same(owner.Lease, leaseBoundContext.GraphUseLease);
        return context.Assemblies.Select(static assembly => assembly.FullName ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
