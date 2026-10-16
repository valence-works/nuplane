using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_UnderOperationRequiresExactActiveTokenAndPreservesWrapperOverlapGuard()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        using var operation = borrow.EnterOperation(registry);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));
        Assert.Equal(0, serializer.ReadCalls);

        var forged = new RootMembershipRegistry.NativeCatalogOwnerBorrow.Operation(borrow);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, forged, CancellationToken.None));
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Throws<PackageStoreAdmissionException>(() => forged.Dispose());
        Assert.Equal(0, serializer.ReadCalls);

        var verified = await registry.VerifyCatalogMemberProtectionUnderOperationAsync(
            borrow, operation, CancellationToken.None);

        Assert.NotEmpty(verified);
        Assert.True(serializer.ReadCalls > 0);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_UnderOperationRejectsExpiredTokenWhileAnotherOperationIsActive()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        var expired = borrow.EnterOperation(registry);
        expired.Dispose();
        using var active = borrow.EnterOperation(registry);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, expired, CancellationToken.None));
        Assert.Equal(0, serializer.ReadCalls);

        var verified = await registry.VerifyCatalogMemberProtectionUnderOperationAsync(
            borrow, active, CancellationToken.None);

        Assert.NotEmpty(verified);
        Assert.True(serializer.ReadCalls > 0);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_UnderOperationRejectsForeignBorrowAndRegistryBeforePayloadRead()
    {
        using var context = await Context.CreateWithGraphAsync();
        using var independentV1 = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var foreignSerializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        var foreignRegistry = new RootMembershipRegistry(context.Files, foreignSerializer);
        await using var firstOwner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var secondOwner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(independentV1.Fixture.PackageInstallRoot), CancellationToken.None);
        await using var firstBorrow = await firstOwner.BorrowAsync(CancellationToken.None);
        await using var secondBorrow = await secondOwner.BorrowAsync(CancellationToken.None);
        using var firstOperation = firstBorrow.EnterOperation(registry);
        using var secondOperation = secondBorrow.EnterOperation(registry);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            registry.VerifyCatalogMemberProtectionUnderOperationAsync(secondBorrow, firstOperation, CancellationToken.None));
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Equal(0, foreignSerializer.ReadCalls);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            foreignRegistry.VerifyCatalogMemberProtectionUnderOperationAsync(firstBorrow, firstOperation, CancellationToken.None));
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Equal(0, foreignSerializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_UnderOperationContinuesAfterBorrowDisposalIsRequested()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        var borrow = await owner.BorrowAsync(CancellationToken.None);
        using var operation = borrow.EnterOperation(registry);

        await borrow.DisposeAsync();
        var verified = await registry.VerifyCatalogMemberProtectionUnderOperationAsync(
            borrow, operation, CancellationToken.None);

        Assert.NotEmpty(verified);
        Assert.True(serializer.ReadCalls > 0);
    }
}
