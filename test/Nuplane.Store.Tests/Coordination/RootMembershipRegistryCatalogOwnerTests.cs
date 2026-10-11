using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipRegistryTests
{
    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_AdoptsRecoverableLocalPendingStateAndRefreshesRetainedEvidence()
    {
        using var context = await Context.CreateAsync(PriorBranch.Acknowledged, complete: true);
        await context.InterruptAsync(RootMembershipPublicationPoint.StatePublished);
        var registry = context.Reopen();
        var catalog = new TestRootCatalog([
            new TrustedPackageStoreRoot("local-pending", context.Fixture.PackageInstallRoot)
        ]);

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(catalog, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var recovered = await registry.RecoverLocalAsync(borrow, context.Initial.RootIdentity, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, recovered.Status);
        Assert.Null(recovered.PendingStateCommit);
        var actualProtection = (await context.ReadStateAsync()).ProtectionRecord;
        Assert.NotNull(context.Next.ProtectionRecord);
        Assert.NotNull(actualProtection);
        Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, actualProtection!.ProtectionDigest);
        Assert.True(context.Next.ProtectionRecord.HasSamePayloadAs(actualProtection));

        await borrow.DisposeAsync();
        await using var refreshedBorrow = await owner.BorrowAsync(CancellationToken.None);
        Assert.NotNull(refreshedBorrow);
    }

    private sealed class TestRootCatalog(IReadOnlyList<TrustedPackageStoreRoot> roots) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; } = Array.AsReadOnly(roots.ToArray());
    }
}
