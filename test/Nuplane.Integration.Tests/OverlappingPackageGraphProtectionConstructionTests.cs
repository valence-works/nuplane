using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Store.Coordination.GraphUseRecords;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task ScopedGraphLoad_ConstructorFailureLeavesLeaseWithCaller(PackageLoadMode mode)
    {
        using var fixture = await GraphUseFixture.CreateAsync(
            packageLoadMode: mode,
            beforeSharedDependencyCompletionMarker: packagePath =>
                File.WriteAllText(Path.Combine(packagePath, "Shared.Dependency.deps.json"), "{"));
        await using var provider = fixture.CreateProvider("member-a", participant: true);
        var graph = fixture.Graphs["member-a"];
        await using var owner = await AcquireGraphUseAsync(provider, graph);
        var publishedUse = Assert.Single((await InspectGraphUseRecordsAsync(
            provider.GetRequiredService<IPackageStoreAdmission>(), fixture)).Entries);
        Assert.Equal(GraphUseRecordOwnershipState.Live, publishedUse.OwnershipState);

        // AssemblyDependencyResolver parses this component manifest while the load-context
        // constructor initializes its resolver list. No assembly has been requested yet.
        var result = await provider.GetRequiredService<IScopedPackageLoader>().EnsureGraphLoadedAsync(
            [graph.CreateEnvelope(owner)], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.NotEmpty(result.FailedByPackageId);
        Assert.False(provider.GetRequiredService<PackageLoader>().TryGetContext("Root.First", "1.0.0", out _));

        // Disposal must still own release authority after the failed constructor. Read the record
        // immediately so a collectible context cannot pass merely because a later GC reaps it.
        await owner.DisposeAsync();
        var releasedUse = Assert.Single((await InspectGraphUseRecordsAsync(
            provider.GetRequiredService<IPackageStoreAdmission>(), fixture)).Entries,
            entry => entry.Record.UseId == publishedUse.Record.UseId);
        Assert.Equal(GraphUseRecordOwnershipState.Stale, releasedUse.OwnershipState);
    }
}
