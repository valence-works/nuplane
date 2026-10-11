using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAdmissionOrderingTests
{
    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmissionHoldsEveryRootAndMemberLockBeforeFirstMemberPayloadRead()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var trackingFiles = scenario.CreateTrackingFileSystem();
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry,
            scenario.First.Fixture.PackageInstallRoot);

        await using var admitted = await admission.AcquireForInstallPathsAsync(
            scenario.InstallPaths, PackageStoreAdmissionKind.Loading);

        Assert.Equal(2, admitted.Entries.Count);
        Assert.All(admitted.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
        Assert.Equal(1, trackingFiles.FirstPayloadReadProbeCount);
        Assert.Equal(roots.Sum(static root => root.LockNames.Count), trackingFiles.LockProbeResults.Count);
        Assert.All(trackingFiles.LockProbeResults, result => Assert.True(result.Busy,
            $"{result.RootLabel}/{result.LockName} was not held when the first member payload was read."));
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmissionAcquiresEveryRootBeforeFirstMemberAndIgnoresRequestOrder()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var trackingFiles = scenario.CreateTrackingFileSystem();
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry,
            scenario.First.Fixture.PackageInstallRoot);

        await using (var forward = await admission.AcquireForInstallPathsAsync(
                         scenario.InstallPaths, PackageStoreAdmissionKind.Loading))
        {
            Assert.All(forward.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
            Assert.Equal(true, trackingFiles.RootLocksBusyAtFirstMemberLock);
            Assert.Equal(0, trackingFiles.PayloadReadCountAtFirstMemberLock);
        }
        var forwardOrder = trackingFiles.LockAcquisitionOrder.ToArray();

        trackingFiles.ResetLockObservations();
        await using (var reverse = await admission.AcquireForInstallPathsAsync(
                         scenario.InstallPaths.Reverse().ToArray(), PackageStoreAdmissionKind.Loading))
        {
            Assert.All(reverse.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
            Assert.Equal(true, trackingFiles.RootLocksBusyAtFirstMemberLock);
            Assert.Equal(0, trackingFiles.PayloadReadCountAtFirstMemberLock);
        }

        Assert.Equal(forwardOrder, trackingFiles.LockAcquisitionOrder);
        var expectedRootOrder = roots
            .OrderBy(root => files.InspectHandle(root.Root).Identity.Provider, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.VolumeOrDeviceId, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.FileId, StringComparer.Ordinal)
            .Select(static root => root.RootLockIdentity)
            .ToArray();
        Assert.Equal(expectedRootOrder, forwardOrder.Take(roots.Length));
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootPathAdmissionRestrictsBorrowsAndRetainsUnionUntilDrainWithUnenrolledPath()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var admission = new PackageStoreAdmission(scenario.Files, scenario.Registry,
            scenario.First.Fixture.PackageInstallRoot);
        var unenrolledPath = scenario.CreateUnenrolledInstallPath();
        var paths = scenario.InstallPaths.Append(unenrolledPath).ToArray();
        await using var admitted = await admission.AcquireForInstallPathsAsync(paths, PackageStoreAdmissionKind.Loading);
        PackageStoreOperationBorrow? firstBorrow = null;
        PackageStoreOperationBorrow? secondBorrow = null;
        Task? closing = null;
        try
        {
            Assert.Equal(
                new[] { PackageStoreAdmissionStatus.Enrolled, PackageStoreAdmissionStatus.Enrolled, PackageStoreAdmissionStatus.Unenrolled },
                admitted.Entries.Select(static entry => entry.Status).ToArray());
            Assert.Null(admitted.Entries[^1].Root);
            firstBorrow = admitted.BorrowFor(paths[0]);
            secondBorrow = admitted.BorrowFor(paths[1]);
            firstBorrow.ValidateForInstallPath(paths[0]);
            secondBorrow.ValidateForInstallPath(paths[1]);
            Assert.Throws<PackageStoreAdmissionException>(() => firstBorrow.ValidateForInstallPath(paths[1]));
            Assert.Throws<PackageStoreAdmissionException>(() => admitted.BorrowFor(unenrolledPath));

            var firstOwner = PackageStoreOperationAccess.GetOwner(firstBorrow);
            var firstContext = PackageStoreOperationAccess.GetLockedMemberLocations(firstOwner, firstBorrow);
            Assert.NotNull(await firstContext.ReadMemberStateAsync("first", CancellationToken.None));

            closing = admitted.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            foreach (var root in scenario.Roots)
            foreach (var lockName in root.LockNames)
                AssertLockBusy(scenario.Files, root, lockName);

            firstBorrow.Dispose();
            firstBorrow = null;
            Assert.False(closing.IsCompleted);
            foreach (var root in scenario.Roots)
            foreach (var lockName in root.LockNames)
                AssertLockBusy(scenario.Files, root, lockName);

            secondBorrow.Dispose();
            secondBorrow = null;
            await closing;
        }
        finally
        {
            secondBorrow?.Dispose();
            firstBorrow?.Dispose();
            if (closing is not null)
                await closing;
        }

        foreach (var root in scenario.Roots)
        foreach (var lockName in root.LockNames)
            AssertLockAvailable(scenario.Files, root, lockName);
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmission_PartialLocatorReplayFailureReleasesTheWholeLockUnion()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var firstRoot = scenario.Roots[0];
        var ledger = scenario.Registry.ReadCandidate(firstRoot.Root);
        Assert.True(ledger.Members.Count >= 2);
        var priorBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(ledger.Members[^2].Binding);
        var secondBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(ledger.Members[^1].Binding);
        var priorStateIdentity = priorBinding.ObservedStateFileIdentity;
        var stateSlot = secondBinding.StateSlot;
        Assert.NotEqual(priorStateIdentity, secondBinding.ObservedStateFileIdentity);
        Assert.NotEqual(priorBinding.StateSlot.ParentIdentity, secondBinding.StateSlot.ParentIdentity);
        var trackingFiles = scenario.CreateTrackingFileSystem(
            failLocatorParentIdentity: stateSlot.ParentIdentity,
            priorLocatorStateIdentity: priorStateIdentity);
        var registry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, registry,
            scenario.First.Fixture.PackageInstallRoot);

        await Assert.ThrowsAsync<IOException>(async () =>
            await admission.AcquireForInstallPathsAsync(scenario.InstallPaths, PackageStoreAdmissionKind.Loading));

        Assert.True(trackingFiles.AllExpectedMemberLocksAcquired);
        Assert.True(trackingFiles.PriorMemberLocatorObserved);
        Assert.True(trackingFiles.LocatorFailureObserved);
        Assert.Equal(scenario.Roots.Sum(static root => root.LockNames.Count),
            trackingFiles.LockAcquisitionOrder.Count);
        Assert.Equal(0, trackingFiles.FirstPayloadReadProbeCount);
        foreach (var root in scenario.Roots)
        foreach (var lockName in root.LockNames)
            AssertLockAvailable(scenario.Files, root, lockName);
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmission_ProfileMutationAfterAllRootsRefusesBeforeMemberLocks()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var profileMutationRoot = roots
            .OrderBy(root => files.InspectHandle(root.Root).Identity.Provider, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.VolumeOrDeviceId, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.FileId, StringComparer.Ordinal)
            .Last();
        var trackingFiles = scenario.CreateTrackingFileSystem(
            profileMutationRootLockIdentity: profileMutationRoot.RootLockIdentity,
            profileMutationControlIdentity: profileMutationRoot.ControlIdentity);
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry,
            scenario.First.Fixture.PackageInstallRoot);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync(scenario.InstallPaths, PackageStoreAdmissionKind.Loading));

        Assert.Equal(roots.Length, trackingFiles.LockAcquisitionOrder.Count);
        Assert.DoesNotContain(trackingFiles.LockAcquisitionOrder, identity =>
            roots.Any(root => root.MemberLockIdentities.Contains(identity)));
        Assert.Equal(0, trackingFiles.FirstPayloadReadProbeCount);
        Assert.All(roots, root => AssertLockAvailable(files, root, "root.lock"));
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmission_LedgerMutationAfterAllRootsRefusesBeforeMemberLocks()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var lastRoot = roots[^1];
        var trackingFiles = scenario.CreateTrackingFileSystem(
            afterLockAcquired: identity =>
            {
                if (identity != lastRoot.RootLockIdentity)
                    return;
                var ledgerPath = Path.Combine(lastRoot.Label,
                    RootMembershipRegistry.ControlDirectoryName, RootMembershipRegistry.LedgerName);
                var ledgerBytes = File.ReadAllBytes(ledgerPath);
                File.WriteAllBytes(ledgerPath, new byte[ledgerBytes.Length]);
            });
        var admission = new PackageStoreAdmission(trackingFiles,
            new RootMembershipRegistry(trackingFiles, new StoreStateSerializer()), scenario.First.Fixture.PackageInstallRoot);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync(scenario.InstallPaths, PackageStoreAdmissionKind.Loading));

        Assert.Equal(roots.Length, trackingFiles.LockAcquisitionOrder.Count);
        Assert.Equal(0, trackingFiles.FirstPayloadReadProbeCount);
        Assert.All(roots, root =>
        {
            foreach (var lockName in root.LockNames)
                AssertLockAvailable(files, root, lockName);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmission_SecondRootBusyUnwindsEarlierRootLock()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var orderedRoots = SortRoots(files, roots);
        var busyRoot = orderedRoots[1];
        using var busyControl = files.OpenDirectoryChildNoFollow(busyRoot.Root, RootMembershipRegistry.ControlDirectoryName);
        using var busyFile = files.OpenFileChildNoFollow(busyControl, "root.lock", FileAccess.ReadWrite);
        var busyOwner = await files.TryAcquireExclusiveLock(busyFile);
        Assert.NotNull(busyOwner);

        var trackingFiles = scenario.CreateTrackingFileSystem();
        var admission = new PackageStoreAdmission(trackingFiles,
            new RootMembershipRegistry(trackingFiles, new StoreStateSerializer()), scenario.First.Fixture.PackageInstallRoot);

        try
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireForInstallPathsAsync(scenario.InstallPaths, PackageStoreAdmissionKind.Loading));
            Assert.Equal(new[] { orderedRoots[0].RootLockIdentity }, trackingFiles.LockAcquisitionOrder);
        }
        finally
        {
            await busyOwner!.DisposeAsync();
        }

        foreach (var root in roots)
        foreach (var lockName in root.LockNames)
            AssertLockAvailable(files, root, lockName);
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmission_LateMemberContentionUnwindsEveryAcquiredLock()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var registry = scenario.Registry;
        var obligations = roots.SelectMany(root =>
        {
            var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root.Root).Identity);
            return registry.ReadCandidate(root.Root).Members.Select(member =>
            {
                var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding);
                return new TestMemberLockObligation(root, rootIdentity, binding.StateSlot,
                    PhysicalStoreLock.GetMemberLockName(binding.StateSlot));
            });
        }).OrderBy(static obligation => obligation.StateSlot, TestStateSlotComparer.Instance)
          .ThenBy(static obligation => obligation.RootIdentity, TestRootIdentityComparer.Instance)
          .ToArray();
        var contended = obligations[^1];
        using var contendedControl = files.OpenDirectoryChildNoFollow(
            contended.Root.Root, RootMembershipRegistry.ControlDirectoryName);
        using var contendedFile = files.OpenFileChildNoFollow(
            contendedControl, contended.LockName, FileAccess.ReadWrite);
        var busyOwner = await files.TryAcquireExclusiveLock(contendedFile);
        Assert.NotNull(busyOwner);

        var trackingFiles = scenario.CreateTrackingFileSystem();
        var admission = new PackageStoreAdmission(trackingFiles,
            new RootMembershipRegistry(trackingFiles, new StoreStateSerializer()), scenario.First.Fixture.PackageInstallRoot);

        try
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireForInstallPathsAsync(scenario.InstallPaths, PackageStoreAdmissionKind.Loading));
            Assert.Equal(roots.Length + obligations.Length - 1, trackingFiles.LockAcquisitionOrder.Count);
            Assert.Equal(0, trackingFiles.FirstPayloadReadProbeCount);
            Assert.Equal(true, trackingFiles.RootLocksBusyAtFirstMemberLock);
            var expectedRootOrder = SortRoots(files, roots).Select(static root => root.RootLockIdentity);
            Assert.Equal(expectedRootOrder, trackingFiles.LockAcquisitionOrder.Take(roots.Length));
        }
        finally
        {
            await busyOwner!.DisposeAsync();
        }

        foreach (var root in roots)
        foreach (var lockName in root.LockNames)
            AssertLockAvailable(files, root, lockName);
    }

    [SupportedPhysicalStoreFact]
    public async Task PhysicalLockUnion_EqualStateSlotRetainsBothRootLocalMemberLocks()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var registry = scenario.Registry;
        var sharedSlot = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
            registry.ReadCandidate(roots[0].Root).Members[0].Binding).StateSlot;
        var sharedLockName = PhysicalStoreLock.GetMemberLockName(sharedSlot);
        using (var secondControl = files.OpenDirectoryChildNoFollow(
                   roots[1].Root, RootMembershipRegistry.ControlDirectoryName))
        {
            if (files.InspectChildNoFollow(secondControl, sharedLockName) is null)
            {
                using var created = files.CreateFileExclusiveAt(secondControl, sharedLockName);
                files.WriteNewControlFile(created, ReadOnlyMemory<byte>.Empty);
            }
        }

        var physicalLock = new PhysicalStoreLock(files);
        var scopes = new List<PhysicalStoreLock.RootLockScope>();
        foreach (var root in roots)
        {
            var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
            try
            {
                var identity = new PhysicalRootIdentity(files.InspectHandle(root.Root).Identity);
                scopes.Add(await physicalLock.AcquireRootLockAsync(control, identity, CancellationToken.None));
                control = null!; // The root scope owns the control handle.
            }
            finally
            {
                control?.Dispose();
            }
        }

        var requests = scopes.Select(scope =>
            new PhysicalStoreLock.RootMemberLockRequest(scope, [sharedSlot])).ToArray();
        await using var union = await physicalLock.AcquireMemberLockUnionAsync(scopes, requests, CancellationToken.None);
        var lockIdentities = roots.Select(root =>
        {
            using var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
            return files.InspectChildNoFollow(control, sharedLockName)!.Identity;
        }).ToArray();
        Assert.NotEqual(lockIdentities[0], lockIdentities[1]);
        Assert.All(roots, root =>
        {
            AssertLockBusy(files, root, "root.lock");
            AssertLockBusy(files, root, sharedLockName);
        });

        await union.DisposeAsync();
        await union.DisposeAsync();
        Assert.All(roots, root =>
        {
            AssertLockAvailable(files, root, "root.lock");
            AssertLockAvailable(files, root, sharedLockName);
        });
        // This test exercises lock mechanics only; it does not create or claim a valid shared-state v1 ledger.
    }

    [SupportedPhysicalStoreFact]
    public async Task PhysicalLockUnion_RetainsLocksUntilEveryCountedRootOwnerDrains()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var registry = scenario.Registry;
        var physicalLock = new PhysicalStoreLock(files);
        var scopes = new List<PhysicalStoreLock.RootLockScope>();
        var ledgers = new List<RootMembershipRecord>();
        foreach (var root in roots)
        {
            var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
            try
            {
                var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root.Root).Identity);
                scopes.Add(await physicalLock.AcquireRootLockAsync(control, rootIdentity, CancellationToken.None));
                control = null!;
                ledgers.Add(registry.ReadCandidate(root.Root));
            }
            finally
            {
                control?.Dispose();
            }
        }

        var memberRequests = scopes.Select((scope, index) =>
            new PhysicalStoreLock.RootMemberLockRequest(scope,
                ledgers[index].Members.Select(member =>
                    Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding).StateSlot).ToArray())).ToArray();
        await using var union = await physicalLock.AcquireMemberLockUnionAsync(scopes, memberRequests, CancellationToken.None);
        var states = roots.Select((root, index) => new PackageStoreOperationState(
            new PhysicalRootIdentity(files.InspectHandle(root.Root).Identity),
            ledgers[index].EnrollmentEpoch,
            union.CreateShare(),
            new NoopPathValidator())).ToArray();
        await using var firstOwner = states[0].Owner;
        await using var secondOwner = states[^1].Owner;
        await union.DisposeAsync(); // Root-state shares now own the common lock lifetime.

        using var borrowOnFirstRoot = states[0].Owner.Borrow();
        await states[^1].Owner.DisposeAsync();
        var firstClose = states[0].Owner.DisposeAsync();
        Assert.False(firstClose.IsCompleted, "The borrowed root owner must wait for its outstanding borrow.");
        Assert.All(roots, root =>
        {
            AssertLockBusy(files, root, "root.lock");
            foreach (var lockName in root.LockNames.Skip(1))
                AssertLockBusy(files, root, lockName);
        });

        borrowOnFirstRoot.Dispose();
        await firstClose;
        await states[0].Owner.DisposeAsync();
        await states[^1].Owner.DisposeAsync();
        await union.DisposeAsync();
        Assert.All(roots, root =>
        {
            foreach (var lockName in root.LockNames)
                AssertLockAvailable(files, root, lockName);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task CancellationWhileAcquiringLaterRootReleasesEarlierRootAndMemberLocks()
    {
        using var scenario = await MultiRootFixture.CreateAsync();
        var files = scenario.Files;
        var roots = scenario.Roots;
        var rootLockIdentities = roots.Select(static root => root.RootLockIdentity).ToHashSet();
        using var cancellation = new CancellationTokenSource();
        var rootLockAcquisitions = 0;
        var trackingFiles = scenario.CreateTrackingFileSystem(
            afterLockAcquired: identity =>
            {
                if (rootLockIdentities.Contains(identity) && Interlocked.Increment(ref rootLockAcquisitions) == 2)
                    cancellation.Cancel();
            });
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry,
            scenario.First.Fixture.PackageInstallRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await admission.AcquireForInstallPathsAsync(
                scenario.InstallPaths, PackageStoreAdmissionKind.Loading, cancellation.Token));

        Assert.Equal(2, rootLockAcquisitions);
        foreach (var root in roots)
        foreach (var lockName in root.LockNames)
        {
            using var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
            using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var owner = await files.TryAcquireExclusiveLock(lockFile);
            Assert.NotNull(owner);
            await owner!.DisposeAsync();
        }
    }

    private static RootProbe CreateRootProbe(
        IPhysicalStoreFileSystem files,
        RootMembershipRegistry registry,
        PhysicalStoreDirectoryHandle root,
        string rootLabel)
    {
        var ledger = registry.ReadCandidate(root);
        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var members = ledger.Members.Select(member =>
        {
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding);
            var lockName = PhysicalStoreLock.GetMemberLockName(binding.StateSlot);
            var lockInfo = files.InspectChildNoFollow(control, lockName);
            Assert.NotNull(lockInfo);
            return (binding.ObservedStateFileIdentity, LockName: lockName, LockIdentity: lockInfo.Identity);
        }).ToArray();

        var rootLock = files.InspectChildNoFollow(control, "root.lock");
        Assert.NotNull(rootLock);
        return new RootProbe(root, rootLabel, rootLock.Identity, files.InspectHandle(control).Identity,
            ["root.lock", .. members.Select(static member => member.LockName)],
            members.Select(static member => member.ObservedStateFileIdentity).ToHashSet(),
            members.Select(static member => member.LockIdentity).ToHashSet());
    }

    private static string[] GetInstallPaths(RootMembershipProtectionVerificationTests.Context context)
        => context.States.Values
            .SelectMany(static state => state.ActivePackageDescriptorsByIdNormalized.Values)
            .Select(static descriptor => descriptor.InstallPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void AssertLockAvailable(
        IPhysicalStoreFileSystem files,
        RootProbe root,
        string lockName)
    {
        using var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
        using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
        var owner = files.TryAcquireExclusiveLock(lockFile).AsTask().GetAwaiter().GetResult();
        Assert.NotNull(owner);
        owner!.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void AssertLockBusy(
        IPhysicalStoreFileSystem files,
        RootProbe root,
        string lockName)
    {
        using var control = files.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
        using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
        Assert.Null(files.TryAcquireExclusiveLock(lockFile).AsTask().GetAwaiter().GetResult());
    }

    private static RootProbe[] SortRoots(IPhysicalStoreFileSystem files, IReadOnlyList<RootProbe> roots)
        => roots.OrderBy(root => files.InspectHandle(root.Root).Identity.Provider, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.VolumeOrDeviceId, StringComparer.Ordinal)
            .ThenBy(root => files.InspectHandle(root.Root).Identity.FileId, StringComparer.Ordinal)
            .ToArray();

    private sealed record TestMemberLockObligation(
        RootProbe Root,
        PhysicalRootIdentity RootIdentity,
        StateSlotIdentity StateSlot,
        string LockName);

    private sealed class TestRootIdentityComparer : IComparer<PhysicalRootIdentity>
    {
        internal static TestRootIdentityComparer Instance { get; } = new();

        public int Compare(PhysicalRootIdentity? left, PhysicalRootIdentity? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = StringComparer.Ordinal.Compare(left.HandleIdentity.Provider, right.HandleIdentity.Provider);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.HandleIdentity.VolumeOrDeviceId, right.HandleIdentity.VolumeOrDeviceId);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.HandleIdentity.FileId, right.HandleIdentity.FileId);
        }
    }

    private sealed class TestStateSlotComparer : IComparer<StateSlotIdentity>
    {
        internal static TestStateSlotComparer Instance { get; } = new();

        public int Compare(StateSlotIdentity? left, StateSlotIdentity? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = StringComparer.Ordinal.Compare(left.ParentIdentity.Provider, right.ParentIdentity.Provider);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.ParentIdentity.VolumeOrDeviceId, right.ParentIdentity.VolumeOrDeviceId);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.ParentIdentity.FileId, right.ParentIdentity.FileId);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.NameSemantics.ProfileId, right.NameSemantics.ProfileId);
            if (result != 0) return result;
            result = left.NameSemantics.Encoding.CompareTo(right.NameSemantics.Encoding);
            if (result != 0) return result;
            result = left.NameSemantics.CaseSensitive.CompareTo(right.NameSemantics.CaseSensitive);
            if (result != 0) return result;
            result = left.NameSemantics.NormalizationInsensitive.CompareTo(right.NameSemantics.NormalizationInsensitive);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.CanonicalBasename, right.CanonicalBasename);
        }
    }

    private sealed class NoopPathValidator : IPackageStoreOperationPathValidator
    {
        public void ValidateForInstallPath(string installPath) => ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
    }

    private sealed class MultiRootFixture : IDisposable
    {
        private readonly PhysicalStoreDirectoryHandle[] _rootHandles;
        private bool _disposed;

        private MultiRootFixture(
            RootMembershipProtectionVerificationTests.Context first,
            RootMembershipProtectionVerificationTests.Context second,
            PackageStoreFixture unenrolledFixture,
            PhysicalStoreDirectoryHandle[] rootHandles,
            RootProbe[] roots,
            RootMembershipRegistry registry)
        {
            First = first;
            Second = second;
            UnenrolledFixture = unenrolledFixture;
            _rootHandles = rootHandles;
            Roots = roots;
            Registry = registry;
        }

        internal RootMembershipProtectionVerificationTests.Context First { get; }
        internal RootMembershipProtectionVerificationTests.Context Second { get; }
        internal PackageStoreFixture UnenrolledFixture { get; }
        internal IPhysicalStoreFileSystem Files => First.Files;
        internal RootProbe[] Roots { get; }
        internal RootMembershipRegistry Registry { get; }
        internal string[] InstallPaths => [GetInstallPaths(First).First(), GetInstallPaths(Second).First()];

        internal static async Task<MultiRootFixture> CreateAsync()
        {
            RootMembershipProtectionVerificationTests.Context? first = null;
            RootMembershipProtectionVerificationTests.Context? second = null;
            PhysicalStoreDirectoryHandle? firstRoot = null;
            PhysicalStoreDirectoryHandle? secondRoot = null;
            PackageStoreFixture? unenrolledFixture = null;
            try
            {
                first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
                second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
                var files = first.Files;
                firstRoot = PhysicalStoreTestDirectory.Open(files, first.Fixture.PackageInstallRoot);
                secondRoot = PhysicalStoreTestDirectory.Open(files, second.Fixture.PackageInstallRoot);
                var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
                var roots = SortRoots(files,
                [
                    CreateRootProbe(files, registry, firstRoot, first.Fixture.PackageInstallRoot),
                    CreateRootProbe(files, registry, secondRoot, second.Fixture.PackageInstallRoot)
                ]);
                var rootHandles = new[] { firstRoot!, secondRoot! };
                unenrolledFixture = new PackageStoreFixture();
                var fixture = new MultiRootFixture(first, second, unenrolledFixture, rootHandles, roots, registry);
                firstRoot = null;
                secondRoot = null;
                unenrolledFixture = null;
                return fixture;
            }
            catch
            {
                unenrolledFixture?.Dispose();
                secondRoot?.Dispose();
                firstRoot?.Dispose();
                second?.Dispose();
                first?.Dispose();
                throw;
            }
        }

        internal FirstMemberPayloadReadTrackingFileSystem CreateTrackingFileSystem(
            Action<PhysicalFileIdentity>? afterLockAcquired = null,
            PhysicalFileIdentity? profileMutationRootLockIdentity = null,
            PhysicalFileIdentity? profileMutationControlIdentity = null,
            PhysicalFileIdentity? failLocatorParentIdentity = null,
            PhysicalFileIdentity? priorLocatorStateIdentity = null)
            => new(Files, Roots.SelectMany(static root => root.MemberStateIdentities).ToHashSet(), Roots,
                afterLockAcquired, profileMutationRootLockIdentity, profileMutationControlIdentity,
                failLocatorParentIdentity, priorLocatorStateIdentity);

        internal string CreateUnenrolledInstallPath()
            => UnenrolledFixture.CreateDirectory("unenrolled/feed/Unenrolled.Package/1.0.0");

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var root in _rootHandles.Reverse())
                root.Dispose();
            UnenrolledFixture.Dispose();
            Second.Dispose();
            First.Dispose();
        }
    }

    private sealed record RootProbe(
        PhysicalStoreDirectoryHandle Root,
        string Label,
        PhysicalFileIdentity RootLockIdentity,
        PhysicalFileIdentity ControlIdentity,
        IReadOnlyList<string> LockNames,
        IReadOnlySet<PhysicalFileIdentity> MemberStateIdentities,
        IReadOnlySet<PhysicalFileIdentity> MemberLockIdentities);

    private sealed record LockProbeResult(string RootLabel, string LockName, bool Busy);

    private sealed class FirstMemberPayloadReadTrackingFileSystem(
        IPhysicalStoreFileSystem inner,
        IReadOnlySet<PhysicalFileIdentity> memberStateIdentities,
        IReadOnlyList<RootProbe> roots,
        Action<PhysicalFileIdentity>? afterLockAcquired = null,
        PhysicalFileIdentity? profileMutationRootLockIdentity = null,
        PhysicalFileIdentity? profileMutationControlIdentity = null,
        PhysicalFileIdentity? failLocatorParentIdentity = null,
        PhysicalFileIdentity? priorLocatorStateIdentity = null)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem
    {
        private readonly object _gate = new();
        private bool _captured;
        private readonly List<LockProbeResult> _lockProbeResults = [];
        private readonly HashSet<PhysicalFileIdentity> _memberLockIdentities = roots
            .SelectMany(static root => root.MemberLockIdentities)
            .ToHashSet();
        private readonly List<PhysicalFileIdentity> _lockAcquisitionOrder = [];
        private bool _firstMemberLockObserved;
        private int _memberLockAcquisitionCount;
        private int _profileMutationEnabled;
        private int _allExpectedMemberLocksAcquired;
        private int _locatorFailureObserved;
        private int _priorMemberLocatorObserved;

        internal int FirstPayloadReadProbeCount { get; private set; }
        internal IReadOnlyList<LockProbeResult> LockProbeResults => _lockProbeResults;
        internal IReadOnlyList<PhysicalFileIdentity> LockAcquisitionOrder
        {
            get
            {
                lock (_gate)
                    return _lockAcquisitionOrder.ToArray();
            }
        }
        internal bool? RootLocksBusyAtFirstMemberLock { get; private set; }
        internal int? PayloadReadCountAtFirstMemberLock { get; private set; }
        internal bool AllExpectedMemberLocksAcquired => Volatile.Read(ref _allExpectedMemberLocksAcquired) != 0;
        internal bool LocatorFailureObserved => Volatile.Read(ref _locatorFailureObserved) != 0;
        internal bool PriorMemberLocatorObserved => Volatile.Read(ref _priorMemberLocatorObserved) != 0;

        internal void ResetLockObservations()
        {
            lock (_gate)
            {
                _captured = false;
                FirstPayloadReadProbeCount = 0;
                _lockProbeResults.Clear();
                _lockAcquisitionOrder.Clear();
                _firstMemberLockObserved = false;
                _memberLockAcquisitionCount = 0;
                RootLocksBusyAtFirstMemberLock = null;
                PayloadReadCountAtFirstMemberLock = null;
                Volatile.Write(ref _allExpectedMemberLocksAcquired, 0);
                Volatile.Write(ref _locatorFailureObserved, 0);
                Volatile.Write(ref _priorMemberLocatorObserved, 0);
            }
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.InspectChildNoFollow(parent, singleName);

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var opened = inner.OpenDirectoryChildNoFollow(parent, singleName);
            if (failLocatorParentIdentity is not null && AllExpectedMemberLocksAcquired &&
                inner.InspectHandle(opened).Identity == failLocatorParentIdentity)
            {
                opened.Dispose();
                Volatile.Write(ref _locatorFailureObserved, 1);
                throw new IOException("Injected partial member-locator replay failure after full union acquisition.");
            }

            return opened;
        }

        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => inner.OpenFileChildNoFollow(parent, singleName, access);

        public string ReadLinkTargetNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);

        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);

        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateDirectoryExclusiveAt(parent, singleName);

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateFileExclusiveAt(parent, singleName);

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            var identity = inner.InspectHandle(file).Identity;
            if (memberStateIdentities.Contains(identity))
            {
                lock (_gate)
                {
                    if (!_captured)
                    {
                        _captured = true;
                        FirstPayloadReadProbeCount++;
                        foreach (var root in roots)
                        foreach (var lockName in root.LockNames)
                            _lockProbeResults.Add(new LockProbeResult(root.Label, lockName,
                                ProbeBusyLock(root, lockName)));
                    }
                }
            }

            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);

        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var owner = await inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (owner is not null)
            {
                var identity = inner.InspectHandle(file).Identity;
                afterLockAcquired?.Invoke(identity);
                if (identity == profileMutationRootLockIdentity)
                    Volatile.Write(ref _profileMutationEnabled, 1);
                var probeFirstMember = false;
                lock (_gate)
                {
                    _lockAcquisitionOrder.Add(identity);
                    if (!_firstMemberLockObserved && _memberLockIdentities.Contains(identity))
                    {
                        _firstMemberLockObserved = true;
                        probeFirstMember = true;
                        PayloadReadCountAtFirstMemberLock = FirstPayloadReadProbeCount;
                    }

                    if (_memberLockIdentities.Contains(identity) &&
                        ++_memberLockAcquisitionCount == _memberLockIdentities.Count)
                        Volatile.Write(ref _allExpectedMemberLocksAcquired, 1);
                }

                if (probeFirstMember)
                    RootLocksBusyAtFirstMemberLock = roots
                        .All(root => ProbeBusyLock(root, "root.lock"));
            }
            return owner;
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
        {
            var semantics = ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
            if (Volatile.Read(ref _profileMutationEnabled) != 0 &&
                inner.InspectHandle(parent).Identity == profileMutationControlIdentity)
            {
                return new PhysicalStoreNameSemantics(semantics.ProfileId + "-mutated",
                    semantics.Encoding, semantics.CaseSensitive, semantics.NormalizationInsensitive);
            }

            return semantics;
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
        {
            if (priorLocatorStateIdentity is not null && AllExpectedMemberLocksAcquired &&
                expectedFileIdentity == priorLocatorStateIdentity)
                Volatile.Write(ref _priorMemberLocatorObserved, 1);

            return ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(
                parent, singleName, expectedFileIdentity);
        }

        public PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName,
            PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);

        public void RemoveControlFileAt(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, singleName, expectedIdentity);

        private bool ProbeBusyLock(RootProbe root, string lockName)
        {
            using var control = inner.OpenDirectoryChildNoFollow(root.Root, RootMembershipRegistry.ControlDirectoryName);
            using var lockFile = inner.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var owner = inner.TryAcquireExclusiveLock(lockFile).AsTask().GetAwaiter().GetResult();
            if (owner is null)
                return true;

            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return false;
        }
    }
}
