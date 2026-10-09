using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAdmissionOrderingTests
{
    [SupportedPhysicalStoreFact]
    public async Task MultiRootAdmissionHoldsEveryRootAndMemberLockBeforeFirstMemberPayloadRead()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();

        // A provider instance can operate on both real fixture trees; handles from both original
        // contexts remain independently owned by their fixtures and are not passed across providers.
        var files = first.Files;
        using var firstRoot = PhysicalStoreTestDirectory.Open(files, first.Fixture.PackageInstallRoot);
        using var secondRoot = PhysicalStoreTestDirectory.Open(files, second.Fixture.PackageInstallRoot);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var roots = new[]
        {
            CreateRootProbe(files, registry, firstRoot, first.Fixture.PackageInstallRoot),
            CreateRootProbe(files, registry, secondRoot, second.Fixture.PackageInstallRoot)
        };
        var memberStateIdentities = roots.SelectMany(static root => root.MemberStateIdentities).ToHashSet();
        var trackingFiles = new FirstMemberPayloadReadTrackingFileSystem(files, memberStateIdentities, roots);
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry, first.Fixture.PackageInstallRoot);
        var installPaths = new[] { GetInstallPaths(first).First(), GetInstallPaths(second).First() };

        await using var admitted = await admission.AcquireForInstallPathsAsync(
            installPaths, PackageStoreAdmissionKind.Loading);

        Assert.Equal(2, admitted.Entries.Count);
        Assert.All(admitted.Entries, entry => Assert.Equal(PackageStoreAdmissionStatus.Enrolled, entry.Status));
        Assert.Equal(1, trackingFiles.FirstPayloadReadProbeCount);
        Assert.Equal(roots.Sum(static root => root.LockNames.Count), trackingFiles.LockProbeResults.Count);
        Assert.All(trackingFiles.LockProbeResults, result => Assert.True(result.Busy,
            $"{result.RootLabel}/{result.LockName} was not held when the first member payload was read."));
    }

    [SupportedPhysicalStoreFact]
    public async Task CancellationWhileAcquiringLaterRootReleasesEarlierRootAndMemberLocks()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var files = first.Files;
        using var firstRoot = PhysicalStoreTestDirectory.Open(files, first.Fixture.PackageInstallRoot);
        using var secondRoot = PhysicalStoreTestDirectory.Open(files, second.Fixture.PackageInstallRoot);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var roots = new[]
        {
            CreateRootProbe(files, registry, firstRoot, first.Fixture.PackageInstallRoot),
            CreateRootProbe(files, registry, secondRoot, second.Fixture.PackageInstallRoot)
        };
        var rootLockIdentities = roots.Select(static root => root.RootLockIdentity).ToHashSet();
        using var cancellation = new CancellationTokenSource();
        var rootLockAcquisitions = 0;
        var trackingFiles = new FirstMemberPayloadReadTrackingFileSystem(files, new HashSet<PhysicalFileIdentity>(), roots,
            identity =>
            {
                if (rootLockIdentities.Contains(identity) && Interlocked.Increment(ref rootLockAcquisitions) == 2)
                    cancellation.Cancel();
            });
        var trackedRegistry = new RootMembershipRegistry(trackingFiles, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(trackingFiles, trackedRegistry, first.Fixture.PackageInstallRoot);
        var installPaths = new[] { GetInstallPaths(first).First(), GetInstallPaths(second).First() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await admission.AcquireForInstallPathsAsync(
                installPaths, PackageStoreAdmissionKind.Loading, cancellation.Token));

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
        var members = ledger.Members.Select(member =>
        {
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding);
            return (binding.ObservedStateFileIdentity, LockName: PhysicalStoreLock.GetMemberLockName(binding.StateSlot));
        }).ToArray();

        using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var rootLock = files.InspectChildNoFollow(control, "root.lock");
        Assert.NotNull(rootLock);
        return new RootProbe(root, rootLabel, rootLock.Identity,
            ["root.lock", .. members.Select(static member => member.LockName)],
            members.Select(static member => member.ObservedStateFileIdentity).ToHashSet());
    }

    private static string[] GetInstallPaths(RootMembershipProtectionVerificationTests.Context context)
        => context.States.Values
            .SelectMany(static state => state.ActivePackageDescriptorsByIdNormalized.Values)
            .Select(static descriptor => descriptor.InstallPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed record RootProbe(
        PhysicalStoreDirectoryHandle Root,
        string Label,
        PhysicalFileIdentity RootLockIdentity,
        IReadOnlyList<string> LockNames,
        IReadOnlySet<PhysicalFileIdentity> MemberStateIdentities);

    private sealed record LockProbeResult(string RootLabel, string LockName, bool Busy);

    private sealed class FirstMemberPayloadReadTrackingFileSystem(
        IPhysicalStoreFileSystem inner,
        IReadOnlySet<PhysicalFileIdentity> memberStateIdentities,
        IReadOnlyList<RootProbe> roots,
        Action<PhysicalFileIdentity>? afterLockAcquired = null)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem
    {
        private readonly object _gate = new();
        private bool _captured;
        private readonly List<LockProbeResult> _lockProbeResults = [];

        internal int FirstPayloadReadProbeCount { get; private set; }
        internal IReadOnlyList<LockProbeResult> LockProbeResults => _lockProbeResults;

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.InspectChildNoFollow(parent, singleName);

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.OpenDirectoryChildNoFollow(parent, singleName);

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
                afterLockAcquired?.Invoke(inner.InspectHandle(file).Identity);
            return owner;
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(
                parent, singleName, expectedFileIdentity);

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
