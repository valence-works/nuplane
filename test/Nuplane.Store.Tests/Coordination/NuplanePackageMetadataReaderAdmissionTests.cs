using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Metadata;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class NuplanePackageMetadataReaderAdmissionTests
{
    private const string ValidMetadata = """{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure"}}""";

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_UsesHeldNativeFileAndRetainsEveryLockThroughByteRead()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var metadataPath = WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, GetMetadataIdentity(context.Files, context.SharedInstallPath));
        var admission = CreateAdmission(context, files);
        var locks = GetLockNames(context);
        PackageStoreOperationOwner? owner = null;
        PackageStoreOperationBorrow? borrow = null;
        var closing = new List<Task>();
        files.OnMetadataRead = () =>
        {
            AssertAllLocksBusy(context, files.Inner, locks);
            borrow!.Dispose();
            closing.Add(owner!.DisposeAsync().AsTask());
            Assert.False(closing[0].IsCompleted);
        };

        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var scopedBorrow = owner.Borrow();
        borrow = scopedBorrow;
        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);
        var closeTask = Assert.Single(closing);
        await closeTask;

        Assert.True(result.IsValid);
        Assert.Equal(1, files.MetadataReadCount);
        Assert.Equal(1, files.MetadataReadCallbackCount);
        Assert.True(closeTask.IsCompletedSuccessfully);
        Assert.True(File.Exists(metadataPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_WhenMetadataIsAbsent_ReturnsMissingWithoutOpeningAPayloadFile()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var files = new TrackingPhysicalStoreFileSystem(context.Files, metadataIdentity: null);
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var borrow = owner.Borrow();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);

        Assert.Equal(NuplanePackageMetadataReadResult.Missing, result);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesHardLinkedMetadataBeforeReadingBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var metadataPath = WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var metadataIdentity = GetMetadataIdentity(context.Files, context.SharedInstallPath);
        CreateHardLink(metadataPath, Path.Combine(context.SharedInstallPath, "metadata-alias.json"));
        var files = new TrackingPhysicalStoreFileSystem(context.Files, metadataIdentity);
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var borrow = owner.Borrow();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, result.AdmissionRefusalReason);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesSymbolicLinkMetadataBeforeReadingBytes()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var targetPath = context.Fixture.GetPath("metadata-target.json");
        File.WriteAllText(targetPath, ValidMetadata);
        File.CreateSymbolicLink(Path.Combine(context.SharedInstallPath, NuplanePackageMetadataReader.MetadataFileName), targetPath);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, metadataIdentity: null);
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var borrow = owner.Borrow();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, result.AdmissionRefusalReason);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_ExpiredBorrowRefusesBeforeOpeningMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, GetMetadataIdentity(context.Files, context.SharedInstallPath));
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        var borrow = owner.Borrow();
        borrow.Dispose();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, result.AdmissionRefusalReason);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesMetadataAliasingIntroducedAfterReadingTheHeldFile()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var metadataPath = WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, GetMetadataIdentity(context.Files, context.SharedInstallPath));
        files.AfterMetadataRead = () => CreateHardLink(metadataPath, metadataPath + ".alias");
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var borrow = owner.Borrow();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", context.SharedInstallPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, result.AdmissionRefusalReason);
        Assert.Equal(1, files.MetadataReadCount);
        Assert.Equal(1, files.AfterMetadataReadCallbackCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesFinalPackageDirectoryAliasBeforeReadingMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, GetMetadataIdentity(context.Files, context.SharedInstallPath));
        var aliasPath = context.SharedInstallPath + "-alias";
        Directory.CreateSymbolicLink(aliasPath, context.SharedInstallPath);
        var admission = CreateAdmission(context, files);
        await using var rootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(rootAdmission.Owner);
        using var borrow = owner.Borrow();

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", aliasPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, result.AdmissionRefusalReason);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_PathBorrowCannotBeReusedForAnUnadmittedAlias()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var files = new TrackingPhysicalStoreFileSystem(context.Files, GetMetadataIdentity(context.Files, context.SharedInstallPath));
        var admission = CreateAdmission(context, files);
        await using var pathAdmission = await admission.AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = pathAdmission.BorrowFor(context.SharedInstallPath);
        var aliasPath = context.SharedInstallPath + "-alias";
        Directory.CreateSymbolicLink(aliasPath, context.SharedInstallPath);

        var result = ((IScopedPackageMetadataReader)new NuplanePackageMetadataReader())
            .Read("Shared.Dependency", "2.1.0", aliasPath, borrow);

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, result.AdmissionRefusalReason);
        Assert.Equal(0, files.MetadataReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesAnEnrolledInstallBeforeParsingItsMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);

        var result = new NuplanePackageMetadataReader().Read(
            "Shared.Dependency", "2.1.0", context.SharedInstallPath);

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, result.AdmissionRefusalReason);
        Assert.False(result.MetadataFound);
        Assert.False(result.IsValid);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesAnIncompleteInstallBeforeParsingItsMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);

        var result = new NuplanePackageMetadataReader().Read(
            "Shared.Dependency", "2.1.0", context.SharedInstallPath);

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, result.AdmissionRefusalReason);
        Assert.False(result.MetadataFound);
        Assert.False(result.IsValid);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesAMissingInstallSuffixBelowCompleteAuthority()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var missingInstallPath = Path.Combine(context.SharedInstallPath, "missing-package");

        var result = new NuplanePackageMetadataReader().Read(
            "Missing.Package", "1.0.0", missingInstallPath);

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, result.AdmissionRefusalReason);
        Assert.False(result.MetadataFound);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesAMissingInstallSuffixBelowIncompleteAuthority()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateAsync();
        var missingInstallPath = Path.Combine(context.SharedInstallPath, "missing-package");

        var result = new NuplanePackageMetadataReader().Read(
            "Missing.Package", "1.0.0", missingInstallPath);

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, result.AdmissionRefusalReason);
        Assert.False(result.MetadataFound);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesAnEnrolledRootAliasBeforeParsingItsMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        WriteMetadata(context.SharedInstallPath, ValidMetadata);
        var aliasRoot = context.Fixture.PackageInstallRoot + "-alias";
        Directory.CreateSymbolicLink(aliasRoot, context.Fixture.PackageInstallRoot);
        var relativeInstallPath = Path.GetRelativePath(context.Fixture.PackageInstallRoot, context.SharedInstallPath);
        var aliasedInstallPath = Path.Combine(aliasRoot, relativeInstallPath);

        var result = new NuplanePackageMetadataReader().Read(
            "Shared.Dependency", "2.1.0", aliasedInstallPath);

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, result.AdmissionRefusalReason);
        Assert.False(result.MetadataFound);
        Assert.False(result.IsValid);
    }

    [SupportedPhysicalStoreFact]
    public void UnscopedRead_WhenTheWholeInstallDirectoryIsMissingBelowUnenrolledAncestry_ReturnsMissing()
    {
        using var fixture = new PackageStoreFixture();
        var missingInstallPath = fixture.GetPath("unmanaged-parent/missing-package");

        var result = new NuplanePackageMetadataReader().Read(
            "Missing.Package", "1.0.0", missingInstallPath);

        Assert.Equal(NuplanePackageMetadataReadResult.Missing, result);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingInstallPath)));
    }

    [SupportedPhysicalStoreFact]
    public void UnscopedRead_PreservesRelativeInstallPathResolution()
    {
        using var fixture = new PackageStoreFixture();
        var relativeInstallPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.PackageInstallRoot);

        var result = new NuplanePackageMetadataReader().Read(
            "Missing.Package", "1.0.0", relativeInstallPath);

        Assert.Equal(NuplanePackageMetadataReadResult.Missing, result);
    }

    [SupportedPhysicalStoreFact]
    public void UnscopedRead_WhenAuthorityAppearsAndCallbackThrows_ReturnsTheTypedReplayRefusal()
    {
        using var fixture = new PackageStoreFixture();
        var missingInstallPath = Path.Combine(fixture.PackageInstallRoot, "future-package");

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory<object?>(
                missingInstallPath,
                (_, availability, directory) =>
                {
                    Assert.Equal(UnenrolledPackageDirectoryStatus.Missing, availability);
                    Assert.Null(directory);
                    Directory.CreateDirectory(Path.Combine(
                        fixture.PackageInstallRoot,
                        RootMembershipRegistry.ControlDirectoryName));
                    throw new IOException("callback failed after authority appeared");
                }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    private static PackageStoreAdmission CreateAdmission(
        RootMembershipProtectionVerificationTests.Context context,
        IPhysicalStoreFileSystem files)
        => new(files, new RootMembershipRegistry(files, new StoreStateSerializer()), context.Fixture.PackageInstallRoot);

    private static string WriteMetadata(string installPath, string metadata)
    {
        var path = Path.Combine(installPath, NuplanePackageMetadataReader.MetadataFileName);
        File.WriteAllText(path, metadata);
        return path;
    }

    private static PhysicalFileIdentity GetMetadataIdentity(IPhysicalStoreFileSystem files, string installPath)
    {
        using var directory = PhysicalStoreTestDirectory.Open(files, installPath);
        using var metadata = files.OpenFileChildNoFollow(directory, NuplanePackageMetadataReader.MetadataFileName, FileAccess.Read);
        return files.InspectHandle(metadata).Identity;
    }

    private static string[] GetLockNames(RootMembershipProtectionVerificationTests.Context context)
    {
        var ledger = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        return ["root.lock", .. ledger.Members.Select(member =>
        {
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding);
            return PhysicalStoreLock.GetMemberLockName(binding.StateSlot);
        })];
    }

    private static void AssertAllLocksBusy(
        RootMembershipProtectionVerificationTests.Context context,
        IPhysicalStoreFileSystem files,
        IReadOnlyList<string> lockNames)
    {
        using var control = files.OpenDirectoryChildNoFollow(context.Root, RootMembershipRegistry.ControlDirectoryName);
        foreach (var lockName in lockNames)
        {
            using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var owner = files.TryAcquireExclusiveLock(lockFile).GetAwaiter().GetResult();
            if (owner is null)
                continue;

            owner.DisposeAsync().GetAwaiter().GetResult();
            Assert.Fail($"The admitted operation did not hold native lock '{lockName}' during the package metadata read.");
        }
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

    private sealed class TrackingPhysicalStoreFileSystem(
        IPhysicalStoreFileSystem inner,
        PhysicalFileIdentity? metadataIdentity)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem
    {
        internal IPhysicalStoreFileSystem Inner => inner;
        internal int MetadataReadCount { get; private set; }
        internal int MetadataReadCallbackCount { get; private set; }
        internal int AfterMetadataReadCallbackCount { get; private set; }
        internal Action? OnMetadataRead { get; set; }
        internal Action? AfterMetadataRead { get; set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => inner.OpenFileChildNoFollow(parent, singleName, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateFileExclusiveAt(parent, singleName);

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (metadataIdentity is not null && inner.InspectHandle(file).Identity == metadataIdentity)
            {
                MetadataReadCount++;
                if (OnMetadataRead is not null)
                    MetadataReadCallbackCount++;
                OnMetadataRead?.Invoke();
            }

            var bytes = inner.ReadControlFile(file, maximumBytes);
            if (metadataIdentity is not null && inner.InspectHandle(file).Identity == metadataIdentity)
            {
                if (AfterMetadataRead is not null)
                    AfterMetadataReadCallbackCount++;
                AfterMetadataRead?.Invoke();
            }
            return bytes;
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent, string stagedName, PhysicalFileIdentity expectedStagedIdentity,
            string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, singleName, expectedIdentity);
    }
}
