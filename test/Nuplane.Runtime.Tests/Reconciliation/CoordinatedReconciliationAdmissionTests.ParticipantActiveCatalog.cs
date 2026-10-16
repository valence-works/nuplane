using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Fact]
    public async Task ParticipantActiveCatalog_ReadsConfiguredMemberThroughCompleteMaintenanceOwner()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var snapshot = await provider.GetRequiredService<IActivePackageCatalog>()
            .GetActivePackagesAsync(CancellationToken.None);

        Assert.Equal(new[] { "Root.First", "Shared.Dependency" },
            snapshot.Packages.Select(static package => package.PackageId).ToArray());
        var expectedPaths = selected.States["first"].ActivePackageDescriptorsByIdNormalized.Values
            .Select(static package => package.InstallPath).OrderBy(static path => path, StringComparer.Ordinal);
        Assert.Equal(expectedPaths,
            snapshot.Packages.Select(static package => package.InstallPath)
                .OrderBy(static path => path, StringComparer.Ordinal));
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParticipantActiveCatalog_DirectResolutionRefusesUnsupportedServiceBeforeStateRead(
        bool replaceRegistry)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var registry = replaceRegistry ? Substitute.For<IStoreRegistry>() : null;
        var admission = replaceRegistry ? null : Substitute.For<IPackageStoreAdmission>();
        using var provider = CreateParticipantCatalogProvider(selected, peer, registry, admission);

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IActivePackageCatalog>());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        if (admission is not null)
        {
            await admission.DidNotReceive().AcquireConfiguredRootOperationAsync(
                Arg.Any<PackageStoreAdmissionKind>(), Arg.Any<CancellationToken>());
        }
        if (registry is not null)
        {
            await registry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ParticipantActiveCatalog_MaintenanceRefusesPendingPeerWithoutRecoveryOrMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await InterruptCatalogStatePublicationAsync(peer);
        var pendingBefore = ReadPendingStateCommit(peer);
        using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IActivePackageCatalog>().GetActivePackagesAsync(CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(pendingBefore.PublicationId, ReadPendingStateCommit(peer).PublicationId);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task ParticipantActiveCatalog_MaintenanceRefusesCorruptPeerWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await File.WriteAllTextAsync(peer.StatePaths["first"], "invalid peer state");
        using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IActivePackageCatalog>().GetActivePackagesAsync(CancellationToken.None));

        Assert.NotEqual(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task ParticipantActiveCatalog_MaintenanceRefusesIncompletePeerWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        ResetMembershipToIncomplete(peer);
        using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<IActivePackageCatalog>().GetActivePackagesAsync(CancellationToken.None));

        Assert.NotEqual(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
        using var root = OpenDirectory(peer.Files, peer.PackageInstallRoot);
        Assert.Equal(RootMembershipStatus.Incomplete,
            new RootMembershipRegistry(peer.Files, new StoreStateSerializer()).ReadCandidate(root).Status);
    }

    private static ServiceProvider CreateParticipantCatalogProvider(
        CompletedMembershipFixture selected,
        CompletedMembershipFixture peer,
        IStoreRegistry? registryOverride = null,
        IPackageStoreAdmission? admissionOverride = null,
        string selectedStateMemberId = "first")
    {
        var members = selected.StatePaths.Select(pair =>
                new PackageStoreMemberDeclaration("default", pair.Key, pair.Value))
            .Concat(peer.StatePaths.Select(pair =>
                new PackageStoreMemberDeclaration("peer", pair.Key, pair.Value)))
            .ToArray();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder =>
        {
            builder.AddPackageStoreRoot("peer", peer.PackageInstallRoot);
            builder.WithStateFile(selected.StatePaths[selectedStateMemberId]);
            builder.UseCoordinatedPackageStore(members, allClientsParticipate: true);
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = selected.PackageInstallRoot);
        if (registryOverride is not null)
            services.AddSingleton(registryOverride);
        if (admissionOverride is not null)
            services.AddSingleton(admissionOverride);
        return services.BuildServiceProvider();
    }

    private static void ResetMembershipToIncomplete(CompletedMembershipFixture fixture)
    {
        Directory.Delete(Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName),
            recursive: true);
        var declarations = fixture.StatePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
            new RootMemberRecord.DeclaredBinding())).ToArray();
        using var root = OpenDirectory(fixture.Files, fixture.PackageInstallRoot);
        new RootMembershipRegistry(fixture.Files, new StoreStateSerializer()).InitializeIncomplete(
            root, fixture.RootIdentity, enrollmentEpoch: 2, declaredMembers: declarations,
            quiescentCutoverConfirmed: true, cancellationToken: CancellationToken.None);
    }
}
