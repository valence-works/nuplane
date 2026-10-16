using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Admin;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicMaintenance_ConfirmedExecuteRecoversStaleUseAndDeletesFreshlyEligibleInstall()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var staleOwner = await AcquireGraphUseAsync(provider, fixture.Graphs["member-a"]);
        await staleOwner.DisposeAsync();

        var retention = new PackageStoreRetentionPolicy(0);
        var useEvidence = CaptureUseArtifactEvidence(fixture);
        Assert.Single(useEvidence, static pair => pair.Key.EndsWith(".json", StringComparison.Ordinal));

        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        Assert.Equal("Preview", preview.Status);
        Assert.Equal("Unknown", preview.ProtectionKnowledge);
        Assert.Contains(preview.Uses, static use => use.OwnershipState == "Stale");
        AssertUseArtifactEvidenceEqual(useEvidence, CaptureUseArtifactEvidence(fixture));

        var inspection = await operations.InspectAsync();
        Assert.Equal("Preview", inspection.Status);
        Assert.Contains(inspection.Uses, static use => use.OwnershipState == "Stale");
        AssertUseArtifactEvidenceEqual(useEvidence, CaptureUseArtifactEvidence(fixture));

        var confirmation = Confirm(preview, retention);
        var wrongEpoch = await operations.PruneAsync(confirmation with
        {
            Confirmation = confirmation.Confirmation! with
            {
                EnrollmentEpoch = confirmation.Confirmation.EnrollmentEpoch + 1
            }
        });
        Assert.Equal("Refused", wrongEpoch.Status);
        Assert.Contains(wrongEpoch.Diagnostics, static diagnostic => diagnostic.Code == "RootMismatch");
        AssertUseArtifactEvidenceEqual(useEvidence, CaptureUseArtifactEvidence(fixture));
        Assert.True(Directory.Exists(fixture.UnrelatedInstall.InstallPath));

        var root = preview.RootIdentity!.HandleIdentity;
        var wrongRoot = await operations.PruneAsync(confirmation with
        {
            Confirmation = confirmation.Confirmation! with
            {
                RootIdentity = new PhysicalRootIdentity(new PhysicalFileIdentity(
                    root.Provider, root.VolumeOrDeviceId, root.FileId + "-changed"))
            }
        });
        Assert.Equal("Refused", wrongRoot.Status);
        Assert.Contains(wrongRoot.Diagnostics, static diagnostic => diagnostic.Code == "RootMismatch");
        AssertUseArtifactEvidenceEqual(useEvidence, CaptureUseArtifactEvidence(fixture));
        Assert.True(Directory.Exists(fixture.UnrelatedInstall.InstallPath));

        var executed = await operations.PruneAsync(confirmation);

        Assert.Equal("Completed", executed.Status);
        Assert.Empty(executed.Uses);
        var unrelated = Assert.Single(executed.Candidates, candidate =>
            candidate.Install.PackageId == fixture.UnrelatedInstall.Id && candidate.Install.Version == fixture.UnrelatedInstall.Version);
        Assert.Equal("Eligible", unrelated.Classification);
        Assert.Equal("Deleted", unrelated.PhysicalOutcome);
        Assert.False(Directory.Exists(fixture.UnrelatedInstall.InstallPath));
        Assert.Empty(CaptureUseArtifactEvidence(fixture));
    }

    [Fact]
    public async Task PublicMaintenance_ExecuteRecoversStaleUseAndPreservesLiveOldGraph()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        await using var memberBProvider = fixture.CreateProvider("member-b", participant: true);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var staleOwner = await AcquireGraphUseAsync(provider, fixture.Graphs["member-a"]);
        var liveOldOwner = await AcquireGraphUseAsync(provider, fixture.Graphs["member-b"]);
        try
        {
            var liveOldRecord = Assert.Single((await InspectGraphUseRecordsAsync(
                provider.GetRequiredService<IPackageStoreAdmission>(), fixture)).Entries,
                entry => entry.Record.GraphSnapshot.GenerationId == liveOldOwner.Lease.Snapshot.GenerationId);
            await PublishMemberBv2Async(memberBProvider, fixture);
            await staleOwner.DisposeAsync();

            var retention = new PackageStoreRetentionPolicy(0);
            var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
            Assert.Contains(preview.Uses, static use => use.OwnershipState == "Stale");
            Assert.Contains(preview.Uses, use => use.UseId == liveOldRecord.Record.UseId &&
                use.OwnershipState == "Live" && use.Graph.GenerationId == liveOldOwner.Lease.Snapshot.GenerationId);

            var executed = await operations.PruneAsync(Confirm(preview, retention));

            Assert.Equal("Completed", executed.Status);
            var retainedUse = Assert.Single(executed.Uses);
            Assert.Equal(liveOldRecord.Record.UseId, retainedUse.UseId);
            Assert.Equal("Live", retainedUse.OwnershipState);
            Assert.Equal(liveOldOwner.Lease.Snapshot.GenerationId, retainedUse.Graph.GenerationId);
            var oldRoot = fixture.Graphs["member-b"].Packages.Single(static package => package.Id == "Root.Second");
            var retainedCandidate = Assert.Single(executed.Candidates, candidate =>
                candidate.Install.PackageId == oldRoot.Id && candidate.Install.Version == oldRoot.Version);
            Assert.Equal("Retained", retainedCandidate.Classification);
            Assert.Equal("Retained", retainedCandidate.PhysicalOutcome);
            Assert.Contains("ProtectedLiveUse", retainedCandidate.Reasons);
            Assert.True(Directory.Exists(oldRoot.InstallPath));
            Assert.All(retainedUse.Graph.Nodes, node => Assert.True(Directory.Exists(Path.Combine(
                fixture.RootPath, node.Install.RootRelativeInstallPath.Replace('/', Path.DirectorySeparatorChar)))));
            var unrelated = Assert.Single(executed.Candidates, candidate =>
                candidate.Install.PackageId == fixture.UnrelatedInstall.Id && candidate.Install.Version == fixture.UnrelatedInstall.Version);
            Assert.Equal("Deleted", unrelated.PhysicalOutcome);
            Assert.False(Directory.Exists(fixture.UnrelatedInstall.InstallPath));
        }
        finally
        {
            await staleOwner.DisposeAsync();
            await liveOldOwner.DisposeAsync();
            await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
        }
    }

    [Fact]
    public async Task PublicMaintenance_ConfirmedExecuteRefusesUnknownUseNamespaceBeforeDeleting()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var staleOwner = await AcquireGraphUseAsync(provider, fixture.Graphs["member-a"]);
        await staleOwner.DisposeAsync();

        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        var confirmation = Confirm(preview, retention);
        var packageEvidence = CaptureFixtureNativeEvidence(fixture);

        var unknownName = $"use-{Guid.NewGuid():N}.unknown";
        using (var control = fixture.Files.OpenDirectoryChildNoFollow(
                   fixture.Root, RootMembershipRegistry.ControlDirectoryName))
        using (var unknown = fixture.Files.CreateFileExclusiveAt(control, unknownName))
            fixture.Files.WriteNewControlFile(unknown, "unrecognized-use-artifact"u8.ToArray());
        var useEvidence = CaptureUseArtifactEvidence(fixture);
        Assert.Contains(unknownName, useEvidence.Keys);

        var refused = await operations.PruneAsync(confirmation);

        Assert.Equal("Refused", refused.Status);
        Assert.NotEmpty(refused.Diagnostics);
        AssertUseArtifactEvidenceEqual(useEvidence, CaptureUseArtifactEvidence(fixture));
        AssertNativeEvidenceEqual(packageEvidence, CaptureFixtureNativeEvidence(fixture));
        Assert.True(Directory.Exists(fixture.UnrelatedInstall.InstallPath));
    }

    private static async Task PublishMemberBv2Async(IServiceProvider memberBProvider, GraphUseFixture fixture)
    {
        await using var operation = await memberBProvider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var borrow = operation.Owner!.Borrow();
        try
        {
            var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(memberBProvider.GetRequiredService<IStoreRegistry>());
            var current = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            var updated = fixture.BuildProtectedState("member-b", fixture.Graphs["member-b-v2"], current,
                checked(current.ProtectionRecord!.Revision + 1), isUpdate: true);
            await registry.PersistCoordinatedActiveStateAsync(borrow, updated, CancellationToken.None);
        }
        finally
        {
            borrow.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, UseArtifactEvidence> CaptureUseArtifactEvidence(GraphUseFixture fixture)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root,
            RootMembershipRegistry.ControlDirectoryName);
        var enumeration = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(fixture.Files);
        var result = new Dictionary<string, UseArtifactEvidence>(StringComparer.Ordinal);
        foreach (var name in enumeration.EnumerateChildNamesNoFollow(control, 4096)
                     .Where(static name => name.StartsWith("use", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = Assert.IsType<PhysicalStoreEntryInfo>(fixture.Files.InspectChildNoFollow(control, name));
            using var file = fixture.Files.OpenFileChildNoFollow(control, name, FileAccess.Read);
            result.Add(name, new UseArtifactEvidence(entry.Identity,
                fixture.Files.ReadControlFile(file, GraphUsePayloadSerializer.MaximumPayloadBytes)));
        }

        return result;
    }

    private static void AssertUseArtifactEvidenceEqual(
        IReadOnlyDictionary<string, UseArtifactEvidence> expected,
        IReadOnlyDictionary<string, UseArtifactEvidence> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            actual.Keys.OrderBy(static name => name, StringComparer.Ordinal));
        foreach (var name in expected.Keys)
        {
            Assert.Equal(expected[name].Identity, actual[name].Identity);
            Assert.Equal(expected[name].Bytes, actual[name].Bytes);
        }
    }

    private sealed record UseArtifactEvidence(PhysicalFileIdentity Identity, byte[] Bytes);
}
