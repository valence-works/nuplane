using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class PackageStoreEnrollmentServiceTests
{
    [SupportedPhysicalStoreFact]
    public async Task EnrollAsync_InitialAbsentLedgerUsesHeldRootIdentityAndPublishesCompleteEmptyState()
    {
        using var fixture = new PackageStoreFixture();
        var files = NativeFiles();
        var serializer = new StoreStateSerializer();
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
        var declarations = new TrustedPackageStoreMemberDeclarationDefinition(true,
            [new TrustedPackageStoreMemberDeclaration("default", "primary", fixture.StateFilePath)]);
        var service = CreateService(files, serializer, fixture.PackageInstallRoot, declarations);
        var request = new PackageStoreEnrollmentOperationRequest("default", rootIdentity, 1, true,
            ["primary"], []);

        var result = await service.EnrollAsync(request, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, result.Status);
        Assert.Equal(rootIdentity, result.RootIdentity);
        Assert.Equal(1, result.EnrollmentEpoch);
        Assert.Equal(PackageStoreEnrollmentMemberDisposition.Enrolled, Assert.Single(result.Members).Disposition);
        var state = await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None);
        Assert.NotNull(state.ProtectionRecord);
        Assert.Equal(rootIdentity, state.ProtectionRecord.RootIdentity);
        Assert.Equal("primary", state.ProtectionRecord.MemberId);
        var ledger = new RootMembershipRegistry(files, serializer).ReadCandidate(root);
        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        Assert.Equal(new[] { "primary" }, ledger.TargetMemberIds);
    }

    [SupportedPhysicalStoreFact]
    public async Task EnrollAsync_NonemptyLegacyStateWithoutVerifiedClosureStaysIncomplete()
    {
        using var fixture = new PackageStoreFixture();
        var files = NativeFiles();
        var serializer = new StoreStateSerializer();
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
        await serializer.SaveAsync(fixture.StateFilePath, StoreStateRecord.Empty() with
        {
            ActiveVersionById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Legacy.Package"] = "1.0.0"
            },
            LastKnownGoodById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Legacy.Package"] = "1.0.0"
            }
        }, CancellationToken.None);
        var declarations = new TrustedPackageStoreMemberDeclarationDefinition(true,
            [new TrustedPackageStoreMemberDeclaration("default", "primary", fixture.StateFilePath)]);
        var service = CreateService(files, serializer, fixture.PackageInstallRoot, declarations);
        var request = new PackageStoreEnrollmentOperationRequest("default", rootIdentity, 1, true,
            ["primary"], []);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => service.EnrollAsync(request, CancellationToken.None));

        var ledger = new RootMembershipRegistry(files, serializer).ReadCandidate(root);
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Null((await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None)).ProtectionRecord);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverEnrollmentAsync_ExactIncompleteBoundRosterCompletesUnderNativeOwner()
    {
        using var fixture = new PackageStoreFixture();
        var files = NativeFiles();
        var serializer = new StoreStateSerializer();
        await serializer.SaveAsync(fixture.StateFilePath, StoreStateRecord.Empty(), CancellationToken.None);
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
        var declaration = new TrustedPackageStoreMemberDeclaration("default", "primary", fixture.StateFilePath);
        var declaredMember = new RootMemberRecord(declaration.MemberId, declaration.StateFileLocator,
            new RootMemberRecord.DeclaredBinding());
        var registry = new RootMembershipRegistry(files, serializer);
        registry.InitializeIncomplete(root, rootIdentity, 1, [declaredMember], true, CancellationToken.None);
        using (var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!))
        {
            await registry.BindDeclaredMembersAsync(root, rootIdentity, 1, [declaredMember],
                new Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>
                {
                    ["primary"] = (parent, Path.GetFileName(fixture.StateFilePath))
                }, true, CancellationToken.None);
        }
        var definitions = new TrustedPackageStoreMemberDeclarationDefinition(true, [declaration]);
        var catalog = new TestRootCatalog(fixture.PackageInstallRoot);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreEnrollmentService(files, registry, serializer, catalog, resolver,
            new TrustedPackageStoreMemberDeclarationCatalog(definitions), new FeedResolutionOptions());
        var request = new PackageStoreEnrollmentOperationRequest("default", rootIdentity, 1, true,
            ["primary"], ["primary"]);

        var result = await service.RecoverEnrollmentAsync(request, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, result.Status);
        Assert.Equal(PackageStoreEnrollmentMemberDisposition.Enrolled, Assert.Single(result.Members).Disposition);
        Assert.NotNull((await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None)).ProtectionRecord);
    }

    private static PackageStoreEnrollmentService CreateService(
        IPhysicalStoreFileSystem files,
        IStoreStateSerializer serializer,
        string rootPath,
        TrustedPackageStoreMemberDeclarationDefinition declarations)
    {
        var catalog = new TestRootCatalog(rootPath);
        var registry = new RootMembershipRegistry(files, serializer);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var memberCatalog = new TrustedPackageStoreMemberDeclarationCatalog(declarations);
        return new PackageStoreEnrollmentService(files, registry, serializer, catalog, resolver, memberCatalog,
            new FeedResolutionOptions());
    }

    private static IPhysicalStoreFileSystem NativeFiles()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private sealed class TestRootCatalog(string rootPath) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; }
            = Array.AsReadOnly([new TrustedPackageStoreRoot("default", rootPath)]);
    }
}
