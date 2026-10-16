using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Store.Tests.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests;

[Trait("Platform", "Native")]
public sealed class PackageStoreInventoryTests
{
    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_RecognizesCompletedInstallsAndClassifiesResiduesWithoutChangingPayloads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var root = context.Fixture.PackageInstallRoot;
        var legacyStaging = context.Fixture.CreateDirectory("packages/.tmp");
        File.WriteAllText(Path.Combine(legacyStaging, "unfinished.part"), "staged data");
        var nativeStage = context.Fixture.CreateDirectory($"packages/feed/Shared.Dependency/.nuplane-stage-{Guid.NewGuid():N}");
        File.WriteAllText(Path.Combine(nativeStage, "attempt.json"), "unfinished native install");
        var prepared = context.Fixture.CreateDirectory($"packages/feed/Shared.Dependency/.nuplane-prepared-{Guid.NewGuid():N}");
        File.WriteAllText(Path.Combine(prepared, "payload.bin"), "prepared data");
        var arbitraryStageLike = context.Fixture.CreateDirectory("packages/feed/Shared.Dependency/.nuplane-stage-not-a-guid");
        var unknownFile = context.Fixture.GetPath("packages/feed/unknown-entry.txt");
        File.WriteAllText(unknownFile, "preserve me");

        var packagePayloads = context.Graphs.Values
            .SelectMany(static graph => graph.Nodes)
            .GroupBy(static node => node.InstallPath!, StringComparer.Ordinal)
            .Select(group => (Path: ToRootRelative(root, group.Key), PackageId: group.First().PackageId))
            .OrderBy(static package => package.Path, StringComparer.Ordinal)
            .ToArray();
        var expectedPaths = packagePayloads.Select(static package => package.Path).ToArray();
        var payloadBytes = packagePayloads.ToDictionary(
            static package => package.Path,
            package => File.ReadAllBytes(Path.Combine(root, package.Path.Replace('/', Path.DirectorySeparatorChar), package.PackageId + ".nuspec")),
            StringComparer.Ordinal);

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var inventory = new PackageStoreInventory(context.Files);

        var snapshot = await inventory.ReadAsync(borrow, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(borrow.Root, snapshot.Root);
        Assert.Equal(borrow.Epoch, snapshot.Epoch);
        var candidates = snapshot.Entries.Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate).ToArray();
        Assert.Equal(expectedPaths, candidates.Select(static entry => entry.RootRelativePath).OrderBy(static path => path, StringComparer.Ordinal));
        Assert.All(candidates, static entry =>
        {
            Assert.NotNull(entry.InstallIdentity);
            Assert.Null(entry.InstallIdentity!.VerifiedArchiveHash);
        });
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.ControlDirectory && entry.RootRelativePath == ".nuplane-store");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.LegacyStagingDirectory && entry.RootRelativePath == ".tmp");
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.NativeStagingResidue && entry.RootRelativePath == ToRootRelative(root, nativeStage));
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.PreparedResidue && entry.RootRelativePath == ToRootRelative(root, prepared));
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == ToRootRelative(root, arbitraryStageLike));
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == ToRootRelative(root, unknownFile));

        var mutableView = Assert.IsAssignableFrom<IList<PackageStoreInventoryEntry>>(snapshot.Entries);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = snapshot.Entries[0]);

        var repeated = await inventory.ReadAsync(borrow, CancellationToken.None);
        Assert.Equal(snapshot.Entries, repeated.Entries);
        Assert.Equal(snapshot.Issues, repeated.Issues);
        foreach (var pair in payloadBytes)
        {
            var packageId = packagePayloads.Single(package => package.Path == pair.Key).PackageId;
            var payloadPath = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar), packageId + ".nuspec");
            Assert.Equal(pair.Value, File.ReadAllBytes(payloadPath));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_InvalidPackageAndCompletionEvidenceRemainsExplicitlyNonCandidate()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        context.Fixture.CreateDirectory("packages/feed/Valid.Package/not-a-version");
        context.Fixture.CreateDirectory("packages/feed/Missing.Marker/1.0.0");
        var nonemptyMarker = context.Fixture.CreateDirectory("packages/feed/Nonempty.Marker/1.0.0");
        File.WriteAllBytes(Path.Combine(nonemptyMarker, PackageInstallStore.CompletionMarkerFileName), [1]);
        var markerDirectory = context.Fixture.CreateDirectory("packages/feed/Directory.Marker/1.0.0");
        Directory.CreateDirectory(Path.Combine(markerDirectory, PackageInstallStore.CompletionMarkerFileName));
        var duplicateVersionRoot = context.Fixture.CreateDirectory("packages/feed/Duplicate.Version");
        foreach (var version in new[] { "1.0", "1.0.0" })
        {
            var path = Directory.CreateDirectory(Path.Combine(duplicateVersionRoot, version)).FullName;
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
        }
        var invalidPackage = context.Fixture.CreateDirectory("packages/feed/Bad Package/1.0.0");
        File.WriteAllBytes(Path.Combine(invalidPackage, PackageInstallStore.CompletionMarkerFileName), []);

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var snapshot = await new PackageStoreInventory(context.Files).ReadAsync(borrow, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Valid.Package/not-a-version");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Missing.Marker/1.0.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Nonempty.Marker/1.0.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Directory.Marker/1.0.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == "feed/Duplicate.Version/1.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == "feed/Duplicate.Version/1.0.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == "feed/Bad Package");
        Assert.DoesNotContain(snapshot.Entries, static entry => entry.RootRelativePath.StartsWith("feed/Bad Package/", StringComparison.Ordinal) &&
            entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate);
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_SameFeedBuildMetadataVersionsRemainSeparateCompletedCandidates()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        foreach (var version in new[] { "2.0.0+alpha", "2.0.0+beta" })
        {
            var installDirectory = context.Fixture.CreateDirectory($"packages/feed/Metadata.Package/{version}");
            File.WriteAllBytes(Path.Combine(installDirectory, PackageInstallStore.CompletionMarkerFileName), []);
        }

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var snapshot = await new PackageStoreInventory(context.Files).ReadAsync(borrow, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        var candidates = snapshot.Entries
            .Where(static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
            .Where(static entry => entry.RootRelativePath.StartsWith("feed/Metadata.Package/", StringComparison.Ordinal))
            .OrderBy(static entry => entry.RootRelativePath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "feed/Metadata.Package/2.0.0+alpha", "feed/Metadata.Package/2.0.0+beta" },
            candidates.Select(static entry => entry.RootRelativePath));
        Assert.Equal(new[] { "2.0.0+alpha", "2.0.0+beta" },
            candidates.Select(static entry => entry.InstallIdentity!.Version));
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_ChangingCompletionMarkerMovesInstallFromCandidateToIncomplete()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var installDirectory = context.Fixture.CreateDirectory("packages/feed/Causal.Package/1.0.0");
        var marker = Path.Combine(installDirectory, PackageInstallStore.CompletionMarkerFileName);
        File.WriteAllBytes(marker, []);

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var inventory = new PackageStoreInventory(context.Files);
        var before = await inventory.ReadAsync(borrow, CancellationToken.None);

        Assert.Contains(before.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate &&
            entry.RootRelativePath == "feed/Causal.Package/1.0.0");

        File.WriteAllBytes(marker, [0x41]);
        var after = await inventory.ReadAsync(borrow, CancellationToken.None);

        Assert.True(after.IsComplete);
        Assert.Contains(after.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall &&
            entry.RootRelativePath == "feed/Causal.Package/1.0.0");
        Assert.DoesNotContain(after.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate &&
            entry.RootRelativePath == "feed/Causal.Package/1.0.0");
    }

    [SupportedUnixFact]
    public async Task ReadAsync_SymbolicLinksAndSpecialEntriesStayUnknownOrIncomplete()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var fixtureTarget = context.Fixture.GetPath("outside-target.txt");
        File.WriteAllText(fixtureTarget, "owned fixture target");
        var linkedEntry = context.Fixture.GetPath("packages/feed/linked-entry");
        File.CreateSymbolicLink(linkedEntry, fixtureTarget);
        var fifo = context.Fixture.GetPath("packages/feed/special-entry");
        CreateFifo(fifo);
        var linkedMarker = context.Fixture.CreateDirectory("packages/feed/Linked.Marker/1.0.0");
        File.CreateSymbolicLink(
            Path.Combine(linkedMarker, PackageInstallStore.CompletionMarkerFileName),
            fixtureTarget);
        var hardLinkedMarker = context.Fixture.CreateDirectory("packages/feed/Hardlinked.Marker/1.0.0");
        var markerPath = Path.Combine(hardLinkedMarker, PackageInstallStore.CompletionMarkerFileName);
        File.WriteAllBytes(markerPath, []);
        CreateHardLink(markerPath, Path.Combine(hardLinkedMarker, "marker-alias"));

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var snapshot = await new PackageStoreInventory(context.Files).ReadAsync(borrow, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == ToRootRelative(context.Fixture.PackageInstallRoot, linkedEntry));
        Assert.Contains(snapshot.Entries, entry => entry.Kind == PackageStoreInventoryEntryKind.Unknown && entry.RootRelativePath == ToRootRelative(context.Fixture.PackageInstallRoot, fifo));
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Linked.Marker/1.0.0");
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.IncompleteInstall && entry.RootRelativePath == "feed/Hardlinked.Marker/1.0.0");
        Assert.DoesNotContain(snapshot.Entries, entry => entry.InstallIdentity?.RootRelativeInstallPath == "feed/Linked.Marker/1.0.0");
        Assert.DoesNotContain(snapshot.Entries, entry => entry.InstallIdentity?.RootRelativeInstallPath == "feed/Hardlinked.Marker/1.0.0");
        Assert.Equal("owned fixture target", File.ReadAllText(fixtureTarget));
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_EmptyCompleteStoreIsPositiveAndDistinctFromAnIncompleteScan()
    {
        using var context = await EmptyStoreContext.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context.Files, context.Registry, context.Fixture.PackageInstallRoot)
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();

        var snapshot = await new PackageStoreInventory(context.Files).ReadAsync(borrow, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        Assert.Empty(snapshot.Issues);
        Assert.DoesNotContain(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate);
        Assert.Contains(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.ControlDirectory);
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_PerDirectoryBoundOverflowReturnsAnIncompleteSnapshotWithLocation()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var crowdedFeed = context.Fixture.CreateDirectory("packages/bulk");
        for (var index = 0; index <= 1024; index++)
            File.WriteAllBytes(Path.Combine(crowdedFeed, $"entry-{index:D4}"), []);

        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var snapshot = await new PackageStoreInventory(context.Files).ReadAsync(borrow, CancellationToken.None);

        Assert.False(snapshot.IsComplete);
        Assert.Contains(snapshot.Issues, static issue => issue.RootRelativePath == "bulk");
        Assert.DoesNotContain(snapshot.Entries, static entry => entry.Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate);
    }

    [SupportedPhysicalStoreFact]
    public async Task ReadAsync_ExpiredAndForeignBorrowsRefuseBeforeReturningInventory()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var foreignContext = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        await using var foreignAdmission = await CreateAdmission(foreignContext).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        var inventory = new PackageStoreInventory(context.Files);

        using var expiredBorrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        expiredBorrow.Dispose();
        var expired = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => inventory.ReadAsync(expiredBorrow, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);

        using var foreignBorrow = Assert.IsType<PackageStoreOperationOwner>(foreignAdmission.Owner).Borrow();
        var foreign = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => inventory.ReadAsync(foreignBorrow, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, foreign.Reason);
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => CreateAdmission(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static PackageStoreAdmission CreateAdmission(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        string packageInstallRoot)
        => new(files, registry, packageInstallRoot);

    private static string ToRootRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void CreateFifo(string path)
    {
        var result = OperatingSystem.IsMacOS()
            ? UnixPath.DarwinMakeFifo(path, 0x180)
            : UnixPath.LinuxMakeFifo(path, 0x180);
        if (result != 0)
            throw new IOException($"Could not create the owned FIFO fixture (native error {Marshal.GetLastPInvokeError()}).");
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsMacOS()
            ? UnixPath.DarwinLink(existingPath, newPath)
            : UnixPath.LinuxLink(existingPath, newPath);
        if (result != 0)
            throw new IOException($"Could not create the owned hard-link fixture (native error {Marshal.GetLastPInvokeError()}).");
    }

    private static class UnixPath
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true)]
        internal static extern int DarwinMakeFifo(string path, uint mode);

        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
        internal static extern int LinuxMakeFifo(string path, uint mode);

        [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
        internal static extern int DarwinLink(string existingPath, string newPath);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        internal static extern int LinuxLink(string existingPath, string newPath);
    }

    private sealed class EmptyStoreContext : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = new();
        internal IPhysicalStoreFileSystem Files { get; } = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal RootMembershipRegistry Registry { get; private set; } = null!;

        internal static async Task<EmptyStoreContext> CreateCompleteAsync()
        {
            var context = new EmptyStoreContext();
            try
            {
                context.Root = PhysicalStoreTestDirectory.Open(context.Files, context.Fixture.PackageInstallRoot);
                context.RootIdentity = new PhysicalRootIdentity(context.Files.InspectHandle(context.Root).Identity);
                var serializer = new StoreStateSerializer();
                context.Registry = new RootMembershipRegistry(context.Files, serializer);
                var statePath = context.Fixture.StateFilePath;
                var stateParentPath = Path.GetDirectoryName(statePath)!;
                var legacyState = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
                await serializer.SaveAsync(statePath, legacyState, CancellationToken.None);
                using var stateParent = PhysicalStoreTestDirectory.Open(context.Files, stateParentPath);

                var members = new[]
                {
                    new RootMemberRecord("empty", statePath, new RootMemberRecord.DeclaredBinding())
                };
                context.Registry.InitializeIncomplete(context.Root, context.RootIdentity, 1, members, true, CancellationToken.None);
                await context.Registry.BindDeclaredMembersAsync(
                    context.Root,
                    context.RootIdentity,
                    1,
                    members,
                    new Dictionary<string, (PhysicalStoreDirectoryHandle, string)>(StringComparer.Ordinal)
                    {
                        ["empty"] = (stateParent, Path.GetFileName(statePath))
                    },
                    true,
                    CancellationToken.None);

                var emptyClosure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
                var state = CreateKnownEmptyState(context.RootIdentity, emptyClosure);
                await context.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
                    context.Root,
                    context.RootIdentity,
                    1,
                    true,
                    async (locked, token) =>
                    {
                        await locked.PublishStateAsync("empty", state, token);
                        return true;
                    },
                    CancellationToken.None);
                await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1, true, CancellationToken.None);
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        private static StoreStateRecord CreateKnownEmptyState(
            PhysicalRootIdentity root,
            PackageProtectionClosure closure)
        {
            var body = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
            var candidate = new PackageProtectionRecord(1, root, 1, "empty", 1,
                ProtectionDigest.StateBody(body), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, root, 1, "empty", 1,
                candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);
            return body with { ProtectionRecord = protection };
        }

        public void Dispose()
        {
            try { Root?.Dispose(); }
            finally { Fixture.Dispose(); }
        }
    }
}
