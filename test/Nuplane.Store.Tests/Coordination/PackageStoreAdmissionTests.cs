using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAdmissionTests
{
    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRetainsVerifiedLocksUntilBorrowDrains()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var admission = CreateAdmission(context);

        var admitted = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, admitted.Status);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admitted.Owner);
        Assert.Equal(context.RootIdentity, owner.Root);
        Assert.Equal(1, owner.Epoch);

        var installPath = GetInstallPaths(context).First();
        var borrow = owner.Borrow();
        borrow.ValidateForInstallPath(installPath);
        var locked = PackageStoreOperationAccess.GetLockedMemberLocations(owner, borrow);
        Assert.NotNull(await locked.ReadMemberStateAsync("first", CancellationToken.None));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation));

        var closing = owner.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => owner.Borrow());
        Assert.Same(locked, PackageStoreOperationAccess.GetLockedMemberLocations(owner, borrow));

        borrow.Dispose();
        await closing;
        Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.GetLockedMemberLocations(owner, borrow));
        Assert.Throws<PackageStoreAdmissionException>(() => borrow.ValidateForInstallPath(installPath));

        var reopened = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        await reopened.DisposeAsync();
        await admitted.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiPathAdmissionDeduplicatesRootAliasesAndRestrictsEachBorrowToItsExactPath()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var admission = CreateAdmission(context);
        var aliasRoot = Path.Combine(context.Fixture.RootPath, "store-alias");
        Directory.CreateSymbolicLink(aliasRoot, context.Fixture.PackageInstallRoot);
        var aliasInstallPath = Path.Combine(aliasRoot, "feed", "Shared.Dependency", "2.1.0");
        var paths = GetInstallPaths(context).Append(aliasInstallPath).ToArray();

        await using var admitted = await admission.AcquireForInstallPathsAsync(paths, PackageStoreAdmissionKind.Loading);
        Assert.Equal(paths.Length, admitted.Entries.Count);
        Assert.All(admitted.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
        Assert.All(admitted.Entries, entry => Assert.Equal(context.RootIdentity, entry.Root));

        var borrows = paths.Select(admitted.BorrowFor).ToArray();
        try
        {
            for (var index = 0; index < paths.Length; index++)
                borrows[index].ValidateForInstallPath(paths[index]);

            var firstOwner = PackageStoreOperationAccess.GetOwner(borrows[0]);
            var firstContext = PackageStoreOperationAccess.GetLockedMemberLocations(firstOwner, borrows[0]);
            Assert.NotNull(await firstContext.ReadMemberStateAsync("first", CancellationToken.None));
            Assert.Same(firstOwner, PackageStoreOperationAccess.GetOwner(borrows[^1]));

            Assert.Throws<PackageStoreAdmissionException>(() =>
                borrows[0].ValidateForInstallPath(paths[1]));
            Assert.Throws<PackageStoreAdmissionException>(() => admitted.BorrowFor(aliasInstallPath + ".missing"));
        }
        finally
        {
            foreach (var borrow in borrows.Reverse())
                borrow.Dispose();
        }

        await admitted.DisposeAsync();
        var reopened = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        await reopened.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmissionHoldsEveryVerifiedMemberSetAndDrainsBeforeUnlock()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var admission = CreateAdmission(first);
        var paths = new[] { GetInstallPaths(first).First(), GetInstallPaths(second).First() };

        var admitted = await admission.AcquireForInstallPathsAsync(paths, PackageStoreAdmissionKind.StartupRecovery);
        Assert.Equal(2, admitted.Entries.Count);
        Assert.All(admitted.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
        Assert.NotEqual(admitted.Entries[0].Root, admitted.Entries[1].Root);

        var borrows = paths.Select(admitted.BorrowFor).ToArray();
        try
        {
            foreach (var borrow in borrows)
            {
                var owner = PackageStoreOperationAccess.GetOwner(borrow);
                borrow.ValidateForInstallPath(paths[Array.IndexOf(borrows, borrow)]);
                var context = PackageStoreOperationAccess.GetLockedMemberLocations(owner, borrow);
                Assert.True(context.Ledger.RootIdentity == first.RootIdentity || context.Ledger.RootIdentity == second.RootIdentity);
            }
            Assert.Throws<PackageStoreAdmissionException>(() =>
            {
                _ = PackageStoreOperationAccess.GetLockedMemberLocations(
                    PackageStoreOperationAccess.GetOwner(borrows[0]), borrows[1]);
            });

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                first.Registry.WithCompleteMemberLocationsAsync(first.Root, first.RootIdentity, 1,
                    (_, _) => Task.FromResult(true), CancellationToken.None));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                second.Registry.WithCompleteMemberLocationsAsync(second.Root, second.RootIdentity, 1,
                    (_, _) => Task.FromResult(true), CancellationToken.None));

            var closing = admitted.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Throws<PackageStoreAdmissionException>(() => admitted.BorrowFor(paths[0]));
            foreach (var borrow in borrows.Reverse())
                borrow.Dispose();
            await closing;
        }
        finally
        {
            foreach (var borrow in borrows.Reverse())
                borrow.Dispose();
            await admitted.DisposeAsync();
        }

        await first.Registry.WithCompleteMemberLocationsAsync(first.Root, first.RootIdentity, 1,
            (_, _) => Task.FromResult(true), CancellationToken.None);
        await second.Registry.WithCompleteMemberLocationsAsync(second.Root, second.RootIdentity, 1,
            (_, _) => Task.FromResult(true), CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task MultiRootRefusalUnwindsEarlierRootWhenLaterSortedRootIsBusy()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var ordered = new[] { first, second }.OrderBy(context => context.RootIdentity, RootIdentityComparer.Instance).ToArray();
        var earlier = ordered[0];
        var later = ordered[1];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = later.Registry.WithCompleteMemberLocationsAsync(later.Root, later.RootIdentity, 1,
            async (_, _) =>
            {
                ready.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return true;
            }, CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var admission = CreateAdmission(first);
            var paths = new[] { GetInstallPaths(earlier).First(), GetInstallPaths(later).First() };
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireForInstallPathsAsync(paths, PackageStoreAdmissionKind.Maintenance));

            // The first sorted root was acquired before the held second root refused; it must have unwound.
            await earlier.Registry.WithCompleteMemberLocationsAsync(earlier.Root, earlier.RootIdentity, 1,
                (_, _) => Task.FromResult(true), CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
            await holder;
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ArchivePathsRequireSingleNativeFileIdentityWithoutArchivePayloadReads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var archivePath = Path.Combine(context.Fixture.PackageInstallRoot, "standalone.nupkg");
        var hardLinkPath = Path.Combine(context.Fixture.PackageInstallRoot, "standalone-copy.nupkg");
        File.WriteAllBytes(archivePath, "archive-payload-must-not-be-read"u8.ToArray());
        using var archive = context.Files.OpenFileChildNoFollow(context.Root, Path.GetFileName(archivePath), FileAccess.Read);
        var identity = context.Files.InspectHandle(archive).Identity;
        var observed = new ArchiveReadCountingFileSystem(context.Files, identity);
        var admission = new PackageStoreAdmission(observed, context.Registry, context.Fixture.PackageInstallRoot);

        await using (var admitted = await admission.AcquireForInstallPathsAsync([archivePath], PackageStoreAdmissionKind.Installation))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, Assert.Single(admitted.Entries).Status);
            using var borrow = admitted.BorrowFor(archivePath);
            borrow.ValidateForInstallPath(archivePath);
            Assert.Equal(0, observed.ArchivePayloadReadCount);
        }

        CreateHardLink(archivePath, hardLinkPath);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync([hardLinkPath], PackageStoreAdmissionKind.Installation));
        Assert.Equal("archive-payload-must-not-be-read", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(archivePath)));
        Assert.Equal(0, observed.ArchivePayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task PostResolutionTargetInspectionFaultClosesTheUntransferredNativeResolution()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var path = GetInstallPaths(context).First();
        using var parent = PhysicalStoreTestDirectory.Open(context.Files, Path.GetDirectoryName(path)!);
        var parentIdentity = context.Files.InspectHandle(parent).Identity;
        var faulting = new TargetInspectionFaultFileSystem(context.Files, parentIdentity, Path.GetFileName(path));
        var admission = new PackageStoreAdmission(faulting, context.Registry, context.Fixture.PackageInstallRoot);

        await Assert.ThrowsAsync<IOException>(async () =>
            await admission.AcquireForInstallPathsAsync([path], PackageStoreAdmissionKind.Installation));
        Assert.NotNull(faulting.TargetHandle);
        Assert.Throws<PackageStoreAdmissionException>(() => context.Files.InspectHandle(faulting.TargetHandle!));
    }

    [SupportedPhysicalStoreFact]
    public async Task PartialMultiRootUnwindReportsBothAdmissionAndNativeReleaseFailures()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var ordered = new[] { first, second }.OrderBy(context => context.RootIdentity, RootIdentityComparer.Instance).ToArray();
        var earlier = ordered[0];
        var later = ordered[1];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = later.Registry.WithCompleteMemberLocationsAsync(later.Root, later.RootIdentity, 1,
            async (_, _) =>
            {
                ready.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return true;
            }, CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var faultingFiles = new LockReleaseFaultFileSystem(first.Files, failFirstRelease: true);
            var registry = new RootMembershipRegistry(faultingFiles, new StoreStateSerializer());
            var admission = new PackageStoreAdmission(faultingFiles, registry, first.Fixture.PackageInstallRoot);
            var paths = new[] { GetInstallPaths(earlier).First(), GetInstallPaths(later).First() };

            var error = await Assert.ThrowsAsync<AggregateException>(async () =>
                await admission.AcquireForInstallPathsAsync(paths, PackageStoreAdmissionKind.Maintenance));
            var flattened = error.Flatten().InnerExceptions;
            Assert.Contains(flattened, exception => exception is PackageStoreAdmissionException);
            Assert.Contains(flattened, exception => exception is IOException && exception.Message.Contains("Injected native lock release failure", StringComparison.Ordinal));

            // The injected release reports failure after unlocking; the earlier root can still be reacquired.
            await earlier.Registry.WithCompleteMemberLocationsAsync(earlier.Root, earlier.RootIdentity, 1,
                (_, _) => Task.FromResult(true), CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
            await holder;
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task FailedNativeOwnerReleaseRemainsVisibleOnRepeatedDisposal()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var faultingFiles = new LockReleaseFaultFileSystem(context.Files, failFirstRelease: true);
        var registry = new RootMembershipRegistry(faultingFiles, new StoreStateSerializer());
        var retained = await registry.AcquireCompleteMemberLocationsAsync(context.Root, context.RootIdentity,
            1, expectedLedgerDigest: null, expectedLedgerIdentity: null, CancellationToken.None);

        var first = await Assert.ThrowsAsync<IOException>(async () => await retained.DisposeAsync());
        Assert.Contains("Injected native lock release failure", first.Message, StringComparison.Ordinal);
        var repeated = await Assert.ThrowsAsync<IOException>(async () => await retained.DisposeAsync());
        Assert.Contains("Injected native lock release failure", repeated.Message, StringComparison.Ordinal);

        // The injected release reports failure after unlocking; the registry can reacquire the entire lock set.
        await context.Registry.WithCompleteMemberLocationsAsync(context.Root, context.RootIdentity, 1,
            (_, _) => Task.FromResult(true), CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnenrolledConfiguredRootIsOnlyReturnedAfterPositiveNativeAbsenceObservation()
    {
        using var fixture = new PackageStoreFixture();
        IPhysicalStoreFileSystem files = OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        using var root = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, fixture.PackageInstallRoot);

        var result = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, result.Status);
        Assert.Null(result.Owner);
        Assert.NotNull(result.Root);

        var install = fixture.CreateDirectory("packages/Example/1.0.0");
        await using var paths = await admission.AcquireForInstallPathsAsync([install], PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, Assert.Single(paths.Entries).Status);
        Assert.Throws<PackageStoreAdmissionException>(() => paths.BorrowFor(install));
    }

    [SupportedPhysicalStoreFact]
    public async Task IncompleteEnrollmentRefusesRootAndPathAdmission()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        await context.PublishStatesAsync();
        var admission = CreateAdmission(context);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync(GetInstallPaths(context), PackageStoreAdmissionKind.Loading));
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => new(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static string[] GetInstallPaths(RootMembershipProtectionVerificationTests.Context context)
        => context.States.Values
            .SelectMany(static state => state.ActivePackageDescriptorsByIdNormalized.Values)
            .Select(static descriptor => descriptor.InstallPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed class RootIdentityComparer : IComparer<PhysicalRootIdentity>
    {
        internal static RootIdentityComparer Instance { get; } = new();

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

    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero))
                throw new IOException($"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
        }
        else if (CreateHardLinkUnix(existingPath, newPath) != 0)
        {
            throw new IOException($"link failed with {Marshal.GetLastPInvokeError()}.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    private abstract class DelegatingPhysicalStoreFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem
    {
        protected IPhysicalStoreFileSystem Inner { get; } = inner;

        public virtual PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => Inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.InspectChildNoFollow(parent, singleName);
        public virtual PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => Inner.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => Inner.OpenFileChildNoFollow(parent, singleName, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => Inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public virtual PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => Inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.CreateFileExclusiveAt(parent, singleName);
        public virtual byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => Inner.ReadControlFile(file, maximumBytes);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)Inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)Inner).RemoveControlFileAt(parent, singleName, expectedIdentity);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => Inner.WriteNewControlFile(file, contents);
        public virtual ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => Inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)Inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)Inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
    }

    private sealed class ArchiveReadCountingFileSystem(
        IPhysicalStoreFileSystem inner,
        PhysicalFileIdentity archiveIdentity)
        : DelegatingPhysicalStoreFileSystem(inner)
    {
        internal int ArchivePayloadReadCount { get; private set; }

        public override byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (Inner.InspectHandle(file).Identity == archiveIdentity)
                ArchivePayloadReadCount++;
            return Inner.ReadControlFile(file, maximumBytes);
        }
    }

    private sealed class TargetInspectionFaultFileSystem(
        IPhysicalStoreFileSystem inner,
        PhysicalFileIdentity targetParentIdentity,
        string targetName)
        : DelegatingPhysicalStoreFileSystem(inner)
    {
        internal PhysicalStoreDirectoryHandle? TargetHandle { get; private set; }

        public override PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var opened = Inner.OpenDirectoryChildNoFollow(parent, singleName);
            if (Inner.InspectHandle(parent).Identity == targetParentIdentity &&
                string.Equals(singleName, targetName, StringComparison.Ordinal))
                TargetHandle = opened;
            return opened;
        }

        public override PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
        {
            if (ReferenceEquals(handle, TargetHandle))
            {
                var caller = new System.Diagnostics.StackTrace().GetFrame(1)?.GetMethod();
                if (caller?.Name == "MoveNext" && caller.DeclaringType?.DeclaringType == typeof(PackageStoreAdmission) &&
                    caller.DeclaringType?.Name.Contains(nameof(PackageStoreAdmission.AcquireForInstallPathsAsync), StringComparison.Ordinal) == true)
                    throw new IOException("Injected final-target metadata failure after path resolution.");
            }
            return Inner.InspectHandle(handle);
        }
    }

    private sealed class LockReleaseFaultFileSystem(IPhysicalStoreFileSystem inner, bool failFirstRelease)
        : DelegatingPhysicalStoreFileSystem(inner)
    {
        private int _failRelease = failFirstRelease ? 1 : 0;

        public override async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var owner = await Inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            return owner is null ? null : new FaultAfterReleaseOwner(owner, this);
        }

        private bool ShouldFailRelease() => Interlocked.Exchange(ref _failRelease, 0) == 1;

        private sealed class FaultAfterReleaseOwner(IAsyncDisposable inner, LockReleaseFaultFileSystem parent) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                if (parent.ShouldFailRelease())
                    throw new IOException("Injected native lock release failure after releasing the lock.");
            }
        }
    }
}
