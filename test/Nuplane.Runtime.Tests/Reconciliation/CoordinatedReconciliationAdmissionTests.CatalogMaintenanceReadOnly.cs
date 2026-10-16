using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CatalogFence_MaintenanceRefusesPendingStateWithoutMutationThenOrdinaryAdmissionRecovers(
        bool pathAdmission,
        bool pendingSelectedRoot)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var pending = pendingSelectedRoot ? selected : peer;
        await InterruptCatalogStatePublicationAsync(pending);
        var pendingCommit = ReadPendingStateCommit(pending);
        var source = new ScopedFileSource(selected.SharedInstallPath, null, null, failAfterRead: false);
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"], source,
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        var before = CaptureFixtureEvidence(selected, peer);

        var error = await Record.ExceptionAsync(async () =>
        {
            if (pathAdmission)
            {
                await using var refused = await admission.AcquireForInstallPathsAsync(
                    [selected.SharedInstallPath], PackageStoreAdmissionKind.Maintenance);
            }
            else
            {
                await using var refused = await admission.AcquireConfiguredRootOperationAsync(
                    PackageStoreAdmissionKind.Maintenance);
            }
        });

        var refusal = Assert.IsType<PackageStoreAdmissionException>(error);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
        var stillPending = ReadPendingStateCommit(pending);
        Assert.Equal(pendingCommit.PublicationId, stillPending.PublicationId);
        Assert.Equal(pendingCommit.Resolution, stillPending.Resolution);

        if (pathAdmission)
        {
            await using var recovered = await admission.AcquireForInstallPathsAsync(
                [selected.SharedInstallPath], PackageStoreAdmissionKind.Reconciliation);
            using var borrow = recovered.BorrowFor(selected.SharedInstallPath);
            Assert.Equal(selected.RootIdentity, borrow.Root);
        }
        else
        {
            await using var recovered = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Reconciliation);
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, recovered.Status);
        }

        using (var root = OpenDirectory(pending.Files, pending.PackageInstallRoot))
        {
            var current = new RootMembershipRegistry(pending.Files, new StoreStateSerializer()).ReadCandidate(root);
            Assert.Equal(RootMembershipStatus.Complete, current.Status);
            Assert.Null(current.PendingStateCommit);
        }
        var recoveredState = await pending.ReadFreshStateAsync(pendingCommit.MemberId);
        Assert.Equal("interrupted", recoveredState.LastFailureById["pending-test"].Message);
        Assert.Equal(pendingCommit.NextProtectionRecord.Revision, recoveredState.ProtectionRecord!.Revision);
        Assert.Equal(pendingCommit.NextProtectionRecord.ProtectionDigest,
            recoveredState.ProtectionRecord.ProtectionDigest);

        if (pathAdmission)
        {
            await using var drained = await admission.AcquireForInstallPathsAsync(
                [selected.SharedInstallPath], PackageStoreAdmissionKind.Maintenance);
            using var borrow = drained.BorrowFor(selected.SharedInstallPath);
            Assert.Equal(selected.RootIdentity, borrow.Root);
        }
        else
        {
            await using var drained = await admission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Maintenance);
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, drained.Status);
        }
    }

    [Fact]
    public async Task CatalogFence_MaintenanceAdmitsSettledCompleteTwoRootCatalogWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, failAfterRead: false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var before = CaptureFixtureEvidence(selected, peer);

        await using (var admitted = await provider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, admitted.Status);
        }

        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    private static PendingStateCommit ReadPendingStateCommit(CompletedMembershipFixture fixture)
    {
        using var root = OpenDirectory(fixture.Files, fixture.PackageInstallRoot);
        var ledger = new RootMembershipRegistry(fixture.Files, new StoreStateSerializer()).ReadCandidate(root);
        return Assert.IsType<PendingStateCommit>(ledger.PendingStateCommit);
    }

    private static IReadOnlyDictionary<string, NativeArtifactEvidence?> CaptureFixtureEvidence(
        params CompletedMembershipFixture[] fixtures)
        => fixtures.SelectMany(CaptureSingleFixtureEvidence)
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, NativeArtifactEvidence?> CaptureSingleFixtureEvidence(
        CompletedMembershipFixture fixture)
    {
        var evidence = new SortedDictionary<string, NativeArtifactEvidence?>(StringComparer.Ordinal);
        var prefix = fixture.Temp.Path + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(fixture.Temp.Path, "*", SearchOption.AllDirectories)
                     .Prepend(fixture.Temp.Path).Order(StringComparer.Ordinal))
        {
            using var opened = OpenDirectory(fixture.Files, directory);
            evidence[prefix + "directory:" + Path.GetRelativePath(fixture.Temp.Path, directory)] =
                new(fixture.Files.InspectHandle(opened).Identity, []);
        }
        foreach (var file in Directory.EnumerateFiles(fixture.Temp.Path, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
            AddFile(Path.GetRelativePath(fixture.Temp.Path, file), file);

        return evidence;

        void AddFile(string key, string path)
        {
            var parentPath = Path.GetDirectoryName(path)!;
            using var parent = OpenDirectory(fixture.Files, parentPath);
            var name = Path.GetFileName(path);
            var observed = fixture.Files.InspectChildNoFollow(parent, name);
            if (observed is null)
            {
                evidence.Add(prefix + key, null);
                return;
            }

            using var file = fixture.Files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
            var held = fixture.Files.InspectHandle(file);
            Assert.Equal(observed.Identity, held.Identity);
            var contents = name.EndsWith(".lock", StringComparison.Ordinal)
                ? Array.Empty<byte>()
                : fixture.Files.ReadControlFile(file, 4 * 1024 * 1024);
            evidence.Add(prefix + key, new(held.Identity, contents));
        }
    }

    private static void AssertFixtureEvidenceEqual(
        IReadOnlyDictionary<string, NativeArtifactEvidence?> expected,
        IReadOnlyDictionary<string, NativeArtifactEvidence?> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var key in expected.Keys)
        {
            var before = expected[key];
            var after = actual[key];
            Assert.Equal(before is null, after is null);
            if (before is null || after is null)
                continue;
            Assert.Equal(before.Identity, after.Identity);
            Assert.Equal(before.Contents, after.Contents);
        }
    }

    private sealed record NativeArtifactEvidence(PhysicalFileIdentity Identity, byte[] Contents);
}
