using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task AddNuplane_RegistersEnrollmentPreviewConcreteAndInternalInterfacesAsOneInstance()
    {
        using var context = await Context.CreateAsync();
        using var provider = CreateRuntimeProvider(context.RootAPath, context.SharedStatePath,
            new CountingDesiredPackageSource());

        var concrete = provider.GetRequiredService<PackageStoreEnrollmentPreviewService>();
        var declarationCatalog = provider.GetRequiredService<TrustedPackageStoreMemberDeclarationCatalog>();

        Assert.Same(concrete, provider.GetRequiredService<IPackageStoreEnrollmentPreviewService>());
        Assert.Same(declarationCatalog, provider.GetRequiredService<ITrustedPackageStoreMemberDeclarationCatalog>());
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_CompleteV2CatalogVerifiesExactSelectedRootAndHistory()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var ledgers = context.ReadCurrentLedgers();
        var declarations = ledgers.SelectMany(ledger => ledger.Members.Select(member =>
            new TrustedPackageStoreMemberDeclaration(
                ledger.RootIdentity == context.RootAIdentity ? "root-0" : "root-1",
                member.MemberId, member.ConfiguredLocator))).ToArray();
        var catalog = Catalog(context.RootAPath, context.RootBPath);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreEnrollmentPreviewService(context.Files, context.Registry(), catalog,
            resolver, new TrustedPackageStoreMemberDeclarationCatalog(
                new TrustedPackageStoreMemberDeclarationDefinition(true, declarations)), new StoreStateSerializer());

        var snapshot = await service.PreviewRootAsync("root-0", CancellationToken.None);
        var observedState = context.ReadSharedState();
        var selectedRow = observedState.ProtectionBundle!.Rows.Single(row => row.RootIdentity == context.RootAIdentity);

        Assert.Equal(PackageStoreEnrollmentPreviewMembership.Complete, snapshot.Membership);
        Assert.Equal(context.RootAIdentity, snapshot.RootIdentity);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.All(snapshot.Members, member =>
        {
            Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
            Assert.NotNull(member.RawPayloadDigest);
            Assert.NotNull(member.StateBodyDigest);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, member.ActiveClosure.Knowledge);
            Assert.Equal(PackageProtectionClosureKnowledge.Known, member.RecoverableClosure.Knowledge);
        });
        Assert.DoesNotContain(snapshot.Members, member => member.MemberId == "shared-b" || member.MemberId == "local-b");
        var shared = snapshot.Members.Single(member => member.MemberId == "shared-a");
        Assert.Equal(selectedRow.ActiveClosure.Graphs!.Select(static graph => graph.GraphId).Order(),
            shared.ActiveClosure.GraphIds!.Order());
        Assert.Equal(new[] { "cross-root-graph" }, shared.ActiveClosure.GraphIds);
        Assert.Equal(selectedRow.RecoverableClosure.Graphs!.Select(static graph => graph.GraphId).Order(),
            shared.RecoverableClosure.GraphIds!.Order());
        Assert.Contains(snapshot.Members, member => member.ActivationHistoryById.Count > 0);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_PendingGroupReportsRecoveryWithoutMutation()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var next = context.PrepareNextGraphPublication();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            next.Descriptor, context.Requests, next.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.RootAIdentity)));

        var ledgers = context.ReadCurrentLedgers();
        var declarations = ledgers.SelectMany(ledger => ledger.Members.Select(member =>
            new TrustedPackageStoreMemberDeclaration(
                ledger.RootIdentity == context.RootAIdentity ? "root-0" : "root-1",
                member.MemberId, member.ConfiguredLocator))).ToArray();
        var catalog = Catalog(context.RootAPath, context.RootBPath);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreEnrollmentPreviewService(context.Files, context.Registry(), catalog,
            resolver, new TrustedPackageStoreMemberDeclarationCatalog(
                new TrustedPackageStoreMemberDeclarationDefinition(true, declarations)), new StoreStateSerializer());
        var statePaths = ledgers.SelectMany(static ledger => ledger.Members)
            .Select(static member => member.ConfiguredLocator).Append(context.SharedStatePath).ToArray();
        var before = PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.RootAPath, context.RootBPath], statePaths.Append(context.StagePath).Append(context.BackupPath));

        var snapshot = await service.PreviewRootAsync("root-0", CancellationToken.None);

        Assert.Equal(ledgers.Single(ledger => ledger.RootIdentity == context.RootAIdentity).Status == RootMembershipStatus.Complete
                ? PackageStoreEnrollmentPreviewMembership.Complete
                : PackageStoreEnrollmentPreviewMembership.Incomplete,
            snapshot.Membership);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.All(snapshot.Members, member =>
        {
            Assert.Equal(PackageStoreEnrollmentPreviewMigration.RecoveryRequired, member.Migration);
            Assert.Equal(PackageStoreEnrollmentPreviewObservation.Observed, member.Observation);
            Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.ActiveClosure.Knowledge);
            Assert.Equal(PackageProtectionClosureKnowledge.Unknown, member.RecoverableClosure.Knowledge);
        });
        var shared = snapshot.Members.Single(member => member.MemberId == "shared-a");
        Assert.Equal(context.NextState.ActiveVersionById, shared.ActiveVersionById);
        Assert.NotEmpty(shared.ActivationHistoryById);
        Assert.Contains("Pending local, group or membership migration publication requires recovery review", Assert.Single(snapshot.Diagnostics));
        var after = PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.RootAPath, context.RootBPath], statePaths.Append(context.StagePath).Append(context.BackupPath));
        Assert.Equal(before, after);
    }

    [SupportedPhysicalStoreFact]
    public async Task PreviewRootAsync_CompleteV2ExplicitKnownEmptyIsDistinctFromLegacyUnknown()
    {
        using var context = await Context.CreateAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var ledgers = context.ReadCurrentLedgers();
        var declarations = ledgers.SelectMany(ledger => ledger.Members.Select(member =>
            new TrustedPackageStoreMemberDeclaration(
                ledger.RootIdentity == context.RootAIdentity ? "root-0" : "root-1",
                member.MemberId, member.ConfiguredLocator))).ToArray();
        var catalog = Catalog(context.RootAPath, context.RootBPath);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreEnrollmentPreviewService(context.Files, context.Registry(), catalog,
            resolver, new TrustedPackageStoreMemberDeclarationCatalog(
                new TrustedPackageStoreMemberDeclarationDefinition(true, declarations)), new StoreStateSerializer());

        var snapshot = await service.PreviewRootAsync("root-0", CancellationToken.None);
        var shared = snapshot.Members.Single(member => member.MemberId == "shared-a");

        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, shared.ActiveClosure.Knowledge);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, shared.RecoverableClosure.Knowledge);
        Assert.Empty(shared.ActiveClosure.GraphIds!);
        Assert.Empty(shared.RecoverableClosure.GraphIds!);
    }
}
