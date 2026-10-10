using System.IO.Compression;
using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageContentAdmissionTests
{
    [SupportedPhysicalStoreFact]
    public async Task UnscopedCoreRefusesCompleteRootBeforeTargetPayloadBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var files = new TrackingFileSystem(context.Files, payloadIdentity);

        var result = PackageContent.TryReadFileUnscoped(context.SharedInstallPath, payloadName, files);

        Assert.Equal(0, files.PayloadReadCount);
        Assert.Null(result);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedCoreRefusesIncompleteRootBeforeTargetPayloadBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var files = new TrackingFileSystem(context.Files, payloadIdentity);

        var result = PackageContent.TryReadFileUnscoped(context.SharedInstallPath, payloadName, files);

        Assert.Equal(0, files.PayloadReadCount);
        Assert.Null(result);
    }

    [SupportedPhysicalStoreFact]
    public async Task LegacyReadRefusesCompleteRootBeforePayloadRead_AndBorrowKeepsLocksThroughDetachedBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var files = new TrackingFileSystem(context.Files, payloadIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);

        Assert.Null(PackageContent.TryReadFile(context.SharedInstallPath, payloadName));
        Assert.Null(PackageContent.TryReadFileUnscoped(context.SharedInstallPath, payloadName, files));
        Assert.Equal(0, files.PayloadReadCount);

        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        var borrow = owner.Borrow();
        Assert.Null(PackageContent.TryReadFile(context.SharedInstallPath, "missing-content.json", borrow));
        Assert.Equal(0, files.PayloadReadCount);
        var found = PackageContent.TryFindByExtension(context.SharedInstallPath, ".nuspec", borrow);
        Assert.Equal(payloadName, found?.Name);
        Assert.Equal(1, files.PayloadReadCount);
        var locks = GetLockNames(context);
        Task? closing = null;
        files.OnPayloadRead = () =>
        {
            AssertAllLocksBusy(context, locks);
            borrow.Dispose();
            closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
        };

        var bytes = PackageContent.TryReadFile(context.SharedInstallPath, payloadName, borrow);

        Assert.NotNull(bytes);
        Assert.Contains("Shared.Dependency", System.Text.Encoding.UTF8.GetString(bytes!));
        Assert.Equal(2, files.PayloadReadCount);
        Assert.NotNull(closing);
        await closing!;
        Assert.True(closing.IsCompletedSuccessfully);
    }

    [SupportedPhysicalStoreFact]
    public async Task PathBorrowAndExpiredBorrowRefuseBeforeDirectoryOrArchivePayloadReads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var otherInstall = context.Fixture.CreateDirectory("packages/feed/Unadmitted.Package/1.0.0");
        File.WriteAllText(Path.Combine(otherInstall, "Unadmitted.Package.nuspec"), "outside admitted union");
        var files = new TrackingFileSystem(context.Files, payloadIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);

        await using var pathAdmission = await admission.AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        var borrow = pathAdmission.BorrowFor(context.SharedInstallPath);
        var wrongPath = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(otherInstall, "Unadmitted.Package.nuspec", borrow));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrongPath.Reason);
        borrow.Dispose();
        var expired = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(context.SharedInstallPath, payloadName, borrow));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        Assert.Equal(0, files.PayloadReadCount);
        Assert.Equal(0, files.ArchiveStreamOpenCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task FinalSymbolicAndHardLinksAreRefusedBeforeContentBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadPath = Path.Combine(context.SharedInstallPath, payloadName);
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var symbolicAlias = Path.Combine(context.SharedInstallPath, "symbolic-alias.json");
        var hardLinkAlias = Path.Combine(context.SharedInstallPath, "hard-link-alias.json");
        File.CreateSymbolicLink(symbolicAlias, payloadPath);
        CreateHardLink(payloadPath, hardLinkAlias);
        var files = new TrackingFileSystem(context.Files, payloadIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);

        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var borrow = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner).Borrow();
        var symbolic = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(context.SharedInstallPath, "symbolic-alias.json", borrow));
        var hardLink = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(context.SharedInstallPath, "hard-link-alias.json", borrow));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, symbolic.Reason);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, hardLink.Reason);
        Assert.Equal(0, files.PayloadReadCount);
        borrow.Dispose();
    }

    [SupportedPhysicalStoreFact]
    public async Task RootBorrowRefusesHardLinkedArchiveBeforeOpeningItsStream()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var archivePath = CreateArchive(context.Fixture.PackageInstallRoot, ("manifest.json", "archive"));
        var archiveIdentity = GetFileIdentity(context.Files,
            Path.GetDirectoryName(archivePath)!, Path.GetFileName(archivePath));
        var hardLink = archivePath + ".alias.nupkg";
        CreateHardLink(archivePath, hardLink);
        var files = new TrackingFileSystem(context.Files, archiveIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var borrow = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner).Borrow();

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(hardLink, "manifest.json", borrow));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, files.ArchiveStreamOpenCount);
        borrow.Dispose();
    }

    [SupportedPhysicalStoreFact]
    public async Task ExactArchiveBorrowReadsFromHeldArchiveAndRetainsOwnerUntilStreamReadCompletes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var archivePath = CreateArchive(context.Fixture.PackageInstallRoot,
            ("manifest.json", "held-archive"), ("lib/ignored.nuspec", "nested"));
        var archiveIdentity = GetFileIdentity(context.Files,
            Path.GetDirectoryName(archivePath)!, Path.GetFileName(archivePath));
        var files = new TrackingFileSystem(context.Files, archiveIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);

        Assert.Null(PackageContent.TryReadFile(archivePath, "manifest.json"));
        Assert.Null(PackageContent.TryFindByExtension(archivePath, ".json"));
        Assert.Null(PackageContent.TryReadFileUnscoped(archivePath, "manifest.json", files));
        Assert.Null(PackageContent.TryFindByExtensionUnscoped(archivePath, ".json", files));
        Assert.Equal(0, files.ArchiveStreamOpenCount);

        await using var pathAdmission = await admission.AcquireForInstallPathsAsync(
            [archivePath], PackageStoreAdmissionKind.Loading);
        var borrow = pathAdmission.BorrowFor(archivePath);
        var otherArchive = CreateArchive(context.Fixture.PackageInstallRoot, ("manifest.json", "wrong archive"));
        var wrongPath = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(otherArchive, "manifest.json", borrow));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrongPath.Reason);
        Assert.Equal(0, files.ArchiveStreamOpenCount);
        var found = PackageContent.TryFindByExtension(archivePath, ".json", borrow);
        Assert.Equal("manifest.json", found?.Name);
        Assert.Equal(1, files.ArchiveStreamOpenCount);
        Assert.Null(PackageContent.TryReadFile(archivePath, "missing.json", borrow));
        Assert.Equal(2, files.ArchiveStreamOpenCount);
        Task? closing = null;
        var owner = GetOperationOwner(borrow);
        files.OnArchiveRead = () =>
        {
            borrow.Dispose();
            closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
        };

        var result = PackageContent.TryReadFile(archivePath, "manifest.json", borrow);

        Assert.Equal("held-archive", System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(result)));
        Assert.Equal(3, files.ArchiveStreamOpenCount);
        Assert.NotNull(closing);
        await closing!;
        Assert.True(closing.IsCompletedSuccessfully);
        var expired = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(archivePath, "manifest.json", borrow));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        Assert.Equal(3, files.ArchiveStreamOpenCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task GraphLeaseReadsExactExtractedInstallAndKeepsCountedPinThroughPayloadRead()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var payloadName = "Shared.Dependency.nuspec";
        var payloadIdentity = GetFileIdentity(context.Files, context.SharedInstallPath, payloadName);
        var files = new TrackingFileSystem(context.Files, payloadIdentity);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);
        await using var observer = new PackageGraphUseLifetimeObserver();
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var operationOwner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        var borrow = operationOwner.Borrow();
        var leaseOwner = await new PackageGraphUseLeaseAcquisition(registry, observer).AcquireForRootAsync(
            borrow,
            context.Graphs["first"],
            context.Requests["first"],
            PackageGraphUseSnapshotState.Committed);
        borrow.Dispose();
        await rootAdmission.DisposeAsync();

        var notInGraph = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageContent.TryReadFile(context.Fixture.PackageInstallRoot, payloadName, leaseOwner.Lease));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, notInGraph.Reason);
        Assert.Equal(0, files.PayloadReadCount);

        var found = PackageContent.TryFindByExtension(context.SharedInstallPath, ".nuspec", leaseOwner.Lease);
        Assert.Equal(payloadName, found?.Name);
        Assert.Equal(1, files.PayloadReadCount);

        Task? closing = null;
        files.OnPayloadRead = () =>
        {
            closing = leaseOwner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
        };
        try
        {
            var bytes = PackageContent.TryReadFile(context.SharedInstallPath, payloadName, leaseOwner.Lease);
            Assert.NotNull(bytes);
            Assert.Contains("Shared.Dependency", System.Text.Encoding.UTF8.GetString(bytes!));
            Assert.Equal(2, files.PayloadReadCount);
            Assert.NotNull(closing);
            await closing!;
            Assert.True(closing.IsCompletedSuccessfully);
        }
        finally
        {
            if (closing is null)
                await leaseOwner.DisposeAsync();
        }
    }

    private static PackageStoreOperationOwner GetOperationOwner(PackageStoreOperationBorrow borrow)
        => PackageStoreOperationAccess.GetOwner(borrow);

    private static string CreateArchive(string root, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(root, $"package-{Guid.NewGuid():N}.nupkg");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private static PhysicalFileIdentity GetFileIdentity(IPhysicalStoreFileSystem files, string parentPath, string name)
    {
        using var parent = PhysicalStoreTestDirectory.Open(files, parentPath);
        using var file = files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        return files.InspectHandle(file).Identity;
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero))
                throw new IOException($"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
            return;
        }

        if (CreateHardLinkUnix(existingPath, newPath) != 0)
            throw new IOException($"link failed with {Marshal.GetLastPInvokeError()}.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);

    private static string[] GetLockNames(RootMembershipProtectionVerificationTests.Context context)
    {
        var ledger = context.Registry.ReadCandidate(context.Root);
        return ["root.lock", .. ledger.Members.Select(member =>
        {
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding);
            return PhysicalStoreLock.GetMemberLockName(binding.StateSlot);
        })];
    }

    private static void AssertAllLocksBusy(
        RootMembershipProtectionVerificationTests.Context context,
        IReadOnlyList<string> lockNames)
    {
        using var control = context.Files.OpenDirectoryChildNoFollow(context.Root, RootMembershipRegistry.ControlDirectoryName);
        foreach (var lockName in lockNames)
        {
            using var lockFile = context.Files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var owner = context.Files.TryAcquireExclusiveLock(lockFile).GetAwaiter().GetResult();
            if (owner is null)
                continue;
            owner.DisposeAsync().GetAwaiter().GetResult();
            Assert.Fail($"The admitted operation did not retain native lock '{lockName}' during PackageContent read.");
        }
    }

    private sealed class TrackingFileSystem(IPhysicalStoreFileSystem inner, PhysicalFileIdentity payloadIdentity)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryEnumerationFileSystem,
            IPhysicalStorePackageStreamFileSystem, IPhysicalStorePublicationFileSystem,
            IPhysicalStoreDirectoryPublicationFileSystem
    {
        internal int PayloadReadCount { get; private set; }
        internal int ArchiveStreamOpenCount { get; private set; }
        internal Action? OnPayloadRead { get; set; }
        internal Action? OnArchiveRead { get; set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName) => inner.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName) => inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory) => inner.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access) => inner.OpenFileChildNoFollow(parent, singleName, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity) => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName) => inner.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName) => inner.CreateFileExclusiveAt(parent, singleName);
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (inner.InspectHandle(file).Identity == payloadIdentity)
            {
                PayloadReadCount++;
                OnPayloadRead?.Invoke();
            }
            return inner.ReadControlFile(file, maximumBytes);
        }
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents) => inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file) => inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => ((IPhysicalStoreDirectoryEnumerationFileSystem)inner).EnumerateChildNamesNoFollow(parent, maximumEntries);
        public Stream OpenPackageArchiveReadStream(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent, PhysicalStoreEntryInfo expectedFile, long maximumBytes)
        {
            if (expectedFile.Identity == payloadIdentity)
            {
                ArchiveStreamOpenCount++;
                OnArchiveRead?.Invoke();
            }
            return ((IPhysicalStorePackageStreamFileSystem)inner).OpenPackageArchiveReadStream(
                parent, singleName, file, expectedParent, expectedFile, maximumBytes);
        }
        public Stream CreatePackageFileWriteStream(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent, long maximumBytes)
            => ((IPhysicalStorePackageStreamFileSystem)inner).CreatePackageFileWriteStream(parent, singleName, file, expectedParent, maximumBytes);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, singleName, expectedIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)inner).ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)inner).PublishDirectoryNoReplaceAt(
                parent, stagedName, expectedStagedIdentity, destinationName);
    }
}
