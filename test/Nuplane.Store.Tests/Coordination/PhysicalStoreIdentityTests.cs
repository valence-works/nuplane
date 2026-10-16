using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PhysicalStoreIdentityTests
{
    [Theory]
    [InlineData(ObserverMismatch.Parent)]
    [InlineData(ObserverMismatch.File)]
    public void ObserveStateSlot_RefusesNameObserverThatBindsDifferentIdentityAndDisposesOpenedFile(
        ObserverMismatch mismatch)
    {
        using var fixture = new IdentityFixture();
        var wrongParent = Identity("different-parent");
        var wrongFile = Identity("different-file");
        fixture.FileSystem.CanonicalName = new PhysicalStoreCanonicalName(
            mismatch == ObserverMismatch.Parent ? wrongParent : fixture.ParentIdentity,
            mismatch == ObserverMismatch.File ? wrongFile : fixture.FileIdentity,
            "store.json",
            fixture.Semantics);

        var error = Assert.Throws<PackageStoreAdmissionException>(
            () => fixture.Identity.ObserveStateSlot(fixture.Parent, "store.json"));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(1, fixture.FileSystem.OpenFileCount);
        Assert.Single(fixture.FileSystem.OpenedFiles);
        Assert.Equal(1, fixture.FileSystem.OpenedFiles[0].ReleaseCount);
    }

    [Fact]
    public void ObserveStateSlot_RefusesCanonicalReopenOfDifferentFileAndDisposesBothFiles()
    {
        using var fixture = new IdentityFixture();
        fixture.FileSystem.CanonicalFileIdentity = Identity("canonical-replacement");

        var error = Assert.Throws<PackageStoreAdmissionException>(
            () => fixture.Identity.ObserveStateSlot(fixture.Parent, "store.json"));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(2, fixture.FileSystem.OpenFileCount);
        Assert.Equal(2, fixture.FileSystem.OpenedFiles.Count);
        Assert.All(fixture.FileSystem.OpenedFiles, handle => Assert.Equal(1, handle.ReleaseCount));
    }

    [Theory]
    [InlineData(ObservationMutation.ParentIdentity)]
    [InlineData(ObservationMutation.OriginalFileIdentity)]
    [InlineData(ObservationMutation.OriginalFileLinkCount)]
    public void ObserveStateSlot_RefusesIdentityOrLinkMutationAndDisposesOpenedFiles(
        ObservationMutation mutation)
    {
        using var fixture = new IdentityFixture();
        fixture.FileSystem.ParentInspection = call => Info(
            PhysicalStoreEntryKind.Directory,
            call == 1 || mutation != ObservationMutation.ParentIdentity
                ? fixture.ParentIdentity
                : Identity("changed-parent"));
        fixture.FileSystem.OriginalFileInspection = call => Info(
            PhysicalStoreEntryKind.RegularFile,
            call == 1 || mutation != ObservationMutation.OriginalFileIdentity
                ? fixture.FileIdentity
                : Identity("changed-file"),
            linkCount: call == 1 || mutation != ObservationMutation.OriginalFileLinkCount ? 1UL : 2UL);

        var error = Assert.Throws<PackageStoreAdmissionException>(
            () => fixture.Identity.ObserveStateSlot(fixture.Parent, "store.json"));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(2, fixture.FileSystem.OpenFileCount);
        Assert.All(fixture.FileSystem.OpenedFiles, handle => Assert.Equal(1, handle.ReleaseCount));
    }

    [Fact]
    public void ObserveStateSlot_ReturnsStableSlotAndSeparateFileRevisionWithoutPayloadOrMutationIo()
    {
        using var fixture = new IdentityFixture();

        var observation = fixture.Identity.ObserveStateSlot(fixture.Parent, "store.json");

        Assert.Equal(fixture.ParentIdentity, observation.Slot.ParentIdentity);
        Assert.Equal("store.json", observation.Slot.CanonicalBasename);
        Assert.Equal(fixture.Semantics, observation.Slot.NameSemantics);
        Assert.Equal(fixture.FileIdentity, observation.FileIdentity);
        Assert.Equal(2, fixture.FileSystem.OpenFileCount);
        Assert.Equal("store.json", fixture.FileSystem.LastOpenedName);
        Assert.Equal(0, fixture.FileSystem.PayloadReadCount);
        Assert.Equal(0, fixture.FileSystem.ControlWriteCount);
        Assert.Equal(0, fixture.FileSystem.CreationCount);
        Assert.Equal(0, fixture.FileSystem.LockCount);
        Assert.All(fixture.FileSystem.OpenedFiles, handle => Assert.Equal(1, handle.ReleaseCount));
    }

    private static PhysicalStoreEntryInfo Info(
        PhysicalStoreEntryKind kind,
        PhysicalFileIdentity identity,
        ulong linkCount = 1)
        => new(kind, identity, linkCount, length: 0);

    private static PhysicalFileIdentity Identity(string fileId)
        => new("test-provider", "test-volume", fileId);

    public enum ObserverMismatch
    {
        Parent,
        File
    }

    public enum ObservationMutation
    {
        ParentIdentity,
        OriginalFileIdentity,
        OriginalFileLinkCount
    }

    private sealed class IdentityFixture : IDisposable
    {
        internal IdentityFixture()
        {
            ParentIdentity = PhysicalStoreIdentityTests.Identity("parent");
            FileIdentity = PhysicalStoreIdentityTests.Identity("state-file");
            Semantics = new PhysicalStoreNameSemantics(
                "test-utf8-ordinal-v1",
                PhysicalStoreNameEncoding.Utf8,
                caseSensitive: true,
                normalizationInsensitive: false);
            FileSystem = new FakePhysicalStoreFileSystem(ParentIdentity, FileIdentity, Semantics);
            Parent = FileSystem.CreateParentHandle();
            Identity = new PhysicalStoreIdentity(FileSystem);
        }

        internal PhysicalFileIdentity ParentIdentity { get; }
        internal PhysicalFileIdentity FileIdentity { get; }
        internal PhysicalStoreNameSemantics Semantics { get; }
        internal FakePhysicalStoreFileSystem FileSystem { get; }
        internal PhysicalStoreDirectoryHandle Parent { get; }
        internal PhysicalStoreIdentity Identity { get; }

        public void Dispose() => Parent.Dispose();
    }

    private sealed class FakePhysicalStoreFileSystem : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem
    {
        private readonly object _providerToken = new();
        private readonly Dictionary<PhysicalStoreHandle, Func<int, PhysicalStoreEntryInfo>> _inspectors = [];
        private readonly Dictionary<PhysicalStoreHandle, int> _inspectionCounts = [];
        private readonly PhysicalFileIdentity _parentIdentity;
        private readonly PhysicalFileIdentity _fileIdentity;
        private readonly PhysicalStoreNameSemantics _semantics;
        private int _nextNativeHandle = 10;

        internal FakePhysicalStoreFileSystem(
            PhysicalFileIdentity parentIdentity,
            PhysicalFileIdentity fileIdentity,
            PhysicalStoreNameSemantics semantics)
        {
            _parentIdentity = parentIdentity;
            _fileIdentity = fileIdentity;
            CanonicalFileIdentity = fileIdentity;
            _semantics = semantics;
            ParentInspection = _ => Info(PhysicalStoreEntryKind.Directory, _parentIdentity);
            OriginalFileInspection = _ => Info(PhysicalStoreEntryKind.RegularFile, _fileIdentity);
            CanonicalFileInspection = _ => Info(PhysicalStoreEntryKind.RegularFile, CanonicalFileIdentity);
        }

        internal Func<int, PhysicalStoreEntryInfo> ParentInspection { get; set; }
        internal Func<int, PhysicalStoreEntryInfo> OriginalFileInspection { get; set; }
        internal Func<int, PhysicalStoreEntryInfo> CanonicalFileInspection { get; set; }
        internal PhysicalStoreCanonicalName? CanonicalName { get; set; }
        internal PhysicalFileIdentity CanonicalFileIdentity { get; set; }
        internal int OpenFileCount { get; private set; }
        internal string? LastOpenedName { get; private set; }
        internal int PayloadReadCount { get; private set; }
        internal int ControlWriteCount { get; private set; }
        internal int CreationCount { get; private set; }
        internal int LockCount { get; private set; }
        internal List<CountingSafeHandle> OpenedFiles { get; } = [];

        internal PhysicalStoreDirectoryHandle CreateParentHandle()
        {
            var native = new CountingSafeHandle(new IntPtr(_nextNativeHandle++));
            var parent = new PhysicalStoreDirectoryHandle(_providerToken, native);
            _inspectors.Add(parent, call => ParentInspection(call));
            return parent;
        }

        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
        {
            if (!_inspectors.TryGetValue(handle, out var inspect))
                throw new InvalidOperationException("The fake received a handle it did not create.");

            var call = _inspectionCounts.GetValueOrDefault(handle) + 1;
            _inspectionCounts[handle] = call;
            return inspect(call);
        }

        public PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            FileAccess access)
        {
            parent.ValidateCreator(_providerToken);
            LastOpenedName = singleName;
            var call = ++OpenFileCount;
            var native = new CountingSafeHandle(new IntPtr(_nextNativeHandle++));
            var handle = new PhysicalStoreFileHandle(
                _providerToken,
                native,
                createdExclusive: false);
            _inspectors.Add(handle, call == 1 ? OriginalFileInspection : CanonicalFileInspection);
            OpenedFiles.Add(native);
            return handle;
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
        {
            parent.ValidateCreator(_providerToken);
            return CanonicalName ?? new PhysicalStoreCanonicalName(
                _parentIdentity,
                expectedFileIdentity,
                singleName,
                _semantics);
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
        {
            parent.ValidateCreator(_providerToken);
            return _semantics;
        }

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => throw Unused();

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
            => throw Unused();

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => throw Unused();

        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => throw Unused();

        public string ReadLinkTargetNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedLinkIdentity)
            => throw Unused();

        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            CreationCount++;
            throw Unused();
        }

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            CreationCount++;
            throw Unused();
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            PayloadReadCount++;
            throw Unused();
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            ControlWriteCount++;
            throw Unused();
        }

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            LockCount++;
            throw Unused();
        }

        private static NotSupportedException Unused()
            => new("This identity test only exercises native metadata observation.");
    }

    private sealed class CountingSafeHandle : SafeHandle
    {
        internal CountingSafeHandle(IntPtr handle)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(handle);
        }

        internal int ReleaseCount { get; private set; }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            ReleaseCount++;
            return true;
        }
    }
}
