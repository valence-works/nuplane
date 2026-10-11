using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Tests.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests;

[Trait("Platform", "Native")]
public sealed class PackageInstallStoreAdmissionTests
{
    [SupportedPhysicalStoreFact]
    public async Task UnenrolledProbeAndHashReadPreservePositiveMissingAndCanonicalHashResults()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.CreateDirectory("packages/feed/Example/1.0.0");
        var markerPath = Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName);
        var hashPath = Path.Combine(installPath, PackageInstallStore.ContentHashFileName);
        var hash = CanonicalHash();

        Assert.False(PackageInstallStore.IsInstalled(installPath));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));

        File.WriteAllBytes(markerPath, []);
        File.WriteAllText(hashPath, hash);
        Assert.True(PackageInstallStore.IsInstalled(installPath));
        Assert.Equal(hash, await PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));

        File.Delete(markerPath);
        Assert.False(PackageInstallStore.IsInstalled(installPath));
        Assert.Equal(hash, await PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));

        var invalidEncoding = hash.ToCharArray();
        invalidEncoding[10] = '!';
        File.WriteAllText(hashPath, new string(invalidEncoding));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));

        var nonCanonicalEncoding = hash.ToCharArray();
        nonCanonicalEncoding[^3] = 'B';
        File.WriteAllText(hashPath, new string(nonCanonicalEncoding));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));

        var missing = fixture.GetPath("packages/feed/Missing/1.0.0");
        Assert.False(PackageInstallStore.IsInstalled(missing));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(missing, CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task PathOnlyProbeAndHashReadRefuseEnrolledPathBeforeReturningPayloadMetadata()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var installPath = context.SharedInstallPath;
        File.WriteAllText(Path.Combine(installPath, PackageInstallStore.ContentHashFileName), CanonicalHash());

        var probe = Assert.Throws<PackageStoreAdmissionException>(() => PackageInstallStore.IsInstalled(installPath));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, probe.Reason);

        var hash = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageInstallStore.ReadContentHashAsync(installPath, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, hash.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedProbeAndHashReadUseTheExactLiveBorrow()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var installPath = context.SharedInstallPath;
        var hash = CanonicalHash();
        File.WriteAllText(Path.Combine(installPath, PackageInstallStore.ContentHashFileName), hash);
        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();

        Assert.True(PackageInstallStore.IsInstalled(installPath, borrow));
        Assert.Equal(hash, await PackageInstallStore.ReadContentHashAsync(installPath, borrow, CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRootBorrowReportsOnlyReplayedMissingInstallSuffixAsNotInstalled()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(context).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(admission.Owner).Borrow();
        var missing = Path.Combine(context.Fixture.PackageInstallRoot, "feed", "Absent.Package", "1.0.0");

        Assert.False(PackageInstallStore.IsInstalled(missing, borrow));
        Assert.Null(await PackageInstallStore.ReadContentHashAsync(missing, borrow, CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedPathBorrowRefusesAnInstallDirectoryThatDisappearsAfterAdmission()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var installPath = context.Fixture.CreateDirectory("packages/unlisted-cache-entry/1.0.0");
        using var parent = PhysicalStoreTestDirectory.Open(context.Files, Path.GetDirectoryName(installPath)!);
        var files = new MissingTargetFileSystem(context.Files,
            context.Files.InspectHandle(parent).Identity, "1.0.0");
        var admission = new PackageStoreAdmission(files, context.Registry, context.Fixture.PackageInstallRoot);
        await using var paths = await admission.AcquireForInstallPathsAsync(
            [installPath], PackageStoreAdmissionKind.Loading);
        using var borrow = paths.BorrowFor(installPath);

        files.HideTarget = true;
        var callbackCalled = false;
        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
                borrow, installPath, (_, _) =>
                {
                    callbackCalled = true;
                    return true;
                }));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.False(callbackCalled);
        var probeRefusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageInstallStore.IsInstalled(installPath, borrow));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, probeRefusal.Reason);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeReclassifiesAStableDirectoryCreatedDuringAbsenceConfirmation()
        => AssertAppearingDirectoryIsReclassifiedBeforeCallback(creationInspection: 2);

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeReclassifiesAStableDirectoryCreatedDuringEvidenceReplay()
        => AssertAppearingDirectoryIsReclassifiedBeforeCallback(creationInspection: 4);

    private static void AssertAppearingDirectoryIsReclassifiedBeforeCallback(int creationInspection)
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/Concurrent/1.0.0");
        var files = InterceptMissingChild(fixture, "packages/feed", "Concurrent");
        var appeared = false;
        files.AfterTargetInspection = entry =>
        {
            if (entry is not null || appeared || files.TargetInspectionCount != creationInspection)
                return;

            Directory.CreateDirectory(installPath);
            File.WriteAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName), []);
            appeared = true;
        };
        var callbackCount = 0;

        var installed = PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
            installPath, files, (nativeFiles, status, directory) =>
            {
                callbackCount++;
                Assert.Equal(UnenrolledPackageDirectoryStatus.Present, status);
                Assert.NotNull(directory);
                using var expected = PhysicalStoreTestDirectory.Open(files.NativeFiles, installPath);
                Assert.Equal(files.NativeFiles.InspectHandle(expected).Identity,
                    files.NativeFiles.InspectHandle(directory!).Identity);
                using var marker = nativeFiles.OpenFileChildNoFollow(directory!,
                    PackageInstallStore.CompletionMarkerFileName, FileAccess.Read);
                Assert.Empty(nativeFiles.ReadControlFile(marker, maximumBytes: 1));
                return true;
            });

        Assert.True(appeared);
        Assert.True(installed);
        Assert.Equal(1, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeRefusesAuthorityCreatedInsideAppearedDirectoryBeforeCallback()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/Authority/1.0.0");
        var files = InterceptMissingChild(fixture, "packages/feed", "Authority");
        var appeared = false;
        files.AfterTargetInspection = entry =>
        {
            if (entry is not null || appeared)
                return;

            Directory.CreateDirectory(Path.Combine(installPath, RootMembershipRegistry.ControlDirectoryName));
            appeared = true;
        };
        var callbackCount = 0;

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath, files, (_, _, _) =>
                {
                    callbackCount++;
                    return true;
                }));

        Assert.True(appeared);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, callbackCount);
        Assert.False(refusal.InnerException is PackageStoreDirectoryAppearanceRetryException);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeRefusesLinkCreatedAtMissingEdgeBeforeCallback()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/Link");
        var external = fixture.CreateDirectory("outside");
        var files = InterceptMissingChild(fixture, "packages/feed", "Link");
        var appeared = false;
        files.AfterTargetInspection = entry =>
        {
            if (entry is not null || appeared)
                return;

            Directory.CreateSymbolicLink(installPath, external);
            appeared = true;
        };
        var callbackCount = 0;

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath, files, (_, _, _) =>
                {
                    callbackCount++;
                    return true;
                }));

        Assert.True(appeared);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeRefusesNameProfileChangeDuringDirectoryAppearanceBeforeCallback()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/ProfileChange");
        var files = InterceptMissingChild(fixture, "packages/feed", "ProfileChange");
        var appeared = false;
        files.AfterTargetInspection = entry =>
        {
            if (entry is not null || appeared || files.TargetInspectionCount != 2)
                return;

            Directory.CreateDirectory(installPath);
            files.ChangeTargetParentNameProfile = true;
            appeared = true;
        };
        var callbackCount = 0;

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath, files, (_, _, _) =>
                {
                    callbackCount++;
                    return true;
                }));

        Assert.True(appeared);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.False(refusal.InnerException is PackageStoreDirectoryAppearanceRetryException);
        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeBoundsRepeatedDirectoryAppearanceChurnBeforeCallback()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/Churn");
        var files = InterceptMissingChild(fixture, "packages/feed", "Churn");
        var appearances = 0;
        PhysicalStoreDirectoryHandle? previousParent = null;
        var classifications = 0;
        files.BeforeTargetInspection = parent =>
        {
            if (ReferenceEquals(parent, previousParent))
                return;

            if (previousParent is not null)
            {
                var expired = Assert.Throws<PackageStoreAdmissionException>(() => files.NativeFiles.InspectHandle(previousParent));
                Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
                Directory.Delete(installPath);
            }
            previousParent = parent;
            classifications++;
        };
        files.AfterTargetInspection = entry =>
        {
            if (entry is not null)
                return;
            Directory.CreateDirectory(installPath);
            appearances++;
        };
        var callbackCount = 0;

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath, files, (_, _, _) =>
                {
                    callbackCount++;
                    return true;
                }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.IsType<PackageStoreDirectoryAppearanceRetryException>(refusal.InnerException);
        Assert.Equal(3, classifications);
        Assert.Equal(3, appearances);
        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledProbeKeepsPostCallbackReplayStrictAndDoesNotRetryCallback()
    {
        using var fixture = new PackageStoreFixture();
        var installPath = fixture.GetPath("packages/feed/Late");
        var files = InterceptMissingChild(fixture, "packages/feed", "Late");
        var callbackCount = 0;

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath, files, (_, status, _) =>
                {
                    callbackCount++;
                    Assert.Equal(UnenrolledPackageDirectoryStatus.Missing, status);
                    Directory.CreateDirectory(installPath);
                    return false;
                }));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.False(refusal.InnerException is PackageStoreDirectoryAppearanceRetryException);
        Assert.Equal(1, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedProbeAndHashReadRefuseWrongAndExpiredBorrows()
    {
        using var first = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        using var second = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admission = await CreateAdmission(first).AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admission.Owner);
        using (var borrow = owner.Borrow())
        {
            var wrongProbe = Assert.Throws<PackageStoreAdmissionException>(() =>
                PackageInstallStore.IsInstalled(second.SharedInstallPath, borrow));
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrongProbe.Reason);

            var wrongHash = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                PackageInstallStore.ReadContentHashAsync(second.SharedInstallPath, borrow, CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrongHash.Reason);
        }

        var expired = owner.Borrow();
        expired.Dispose();
        var expiredProbe = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageInstallStore.IsInstalled(first.SharedInstallPath, expired));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expiredProbe.Reason);

        var expiredHash = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageInstallStore.ReadContentHashAsync(first.SharedInstallPath, expired, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expiredHash.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeProbeAndHashReadRefuseHardLinkedMetadataFiles()
    {
        using var fixture = new PackageStoreFixture();
        var markerInstall = fixture.CreateDirectory("packages/feed/Marker/1.0.0");
        var marker = Path.Combine(markerInstall, PackageInstallStore.CompletionMarkerFileName);
        var markerAlias = Path.Combine(markerInstall, "marker-alias");
        File.WriteAllBytes(markerAlias, []);
        CreateHardLink(markerAlias, marker);

        var markerError = Assert.Throws<PackageStoreAdmissionException>(() => PackageInstallStore.IsInstalled(markerInstall));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, markerError.Reason);

        var hashInstall = fixture.CreateDirectory("packages/feed/Hash/1.0.0");
        var hashAlias = Path.Combine(hashInstall, "hash-alias");
        var hash = CanonicalHash();
        File.WriteAllText(hashAlias, hash);
        CreateHardLink(hashAlias, Path.Combine(hashInstall, PackageInstallStore.ContentHashFileName));
        var hashError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageInstallStore.ReadContentHashAsync(hashInstall, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, hashError.Reason);

        var linkedMarkerInstall = fixture.CreateDirectory("packages/feed/LinkedMarker/1.0.0");
        var markerTarget = fixture.GetPath("marker-target");
        File.WriteAllBytes(markerTarget, []);
        File.CreateSymbolicLink(Path.Combine(linkedMarkerInstall, PackageInstallStore.CompletionMarkerFileName), markerTarget);
        var linkedMarkerError = Assert.Throws<PackageStoreAdmissionException>(() =>
            PackageInstallStore.IsInstalled(linkedMarkerInstall));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, linkedMarkerError.Reason);

        var linkedHashInstall = fixture.CreateDirectory("packages/feed/LinkedHash/1.0.0");
        var hashTarget = fixture.GetPath("hash-target");
        File.WriteAllText(hashTarget, hash);
        File.CreateSymbolicLink(Path.Combine(linkedHashInstall, PackageInstallStore.ContentHashFileName), hashTarget);
        var linkedHashError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            PackageInstallStore.ReadContentHashAsync(linkedHashInstall, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, linkedHashError.Reason);
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => new(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static MissingTargetFileSystem InterceptMissingChild(
        PackageStoreFixture fixture,
        string parentRelativePath,
        string targetName)
    {
        var files = PackageStoreRuntimeAdmission.CreatePhysicalFileSystem();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.CreateDirectory(parentRelativePath));
        var parentIdentity = files.InspectHandle(parent).Identity;
        return new MissingTargetFileSystem(files, parentIdentity, targetName);
    }

    private static string CanonicalHash() => "sha512:" + Convert.ToBase64String(new byte[64]);

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

    private sealed class MissingTargetFileSystem(
        IPhysicalStoreFileSystem inner,
        PhysicalFileIdentity parentIdentity,
        string targetName) : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem
    {
        internal bool HideTarget { get; set; }
        internal bool ChangeTargetParentNameProfile { get; set; }
        internal Action<PhysicalStoreDirectoryHandle>? BeforeTargetInspection { get; set; }
        internal Action<PhysicalStoreEntryInfo?>? AfterTargetInspection { get; set; }
        internal IPhysicalStoreFileSystem NativeFiles => inner;
        internal int TargetInspectionCount { get; private set; }
        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
            => inner.OpenNamespaceRoot(anchor);

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var matchesTarget = inner.InspectHandle(parent).Identity == parentIdentity &&
                string.Equals(singleName, targetName, StringComparison.Ordinal);
            if (matchesTarget)
            {
                TargetInspectionCount++;
                BeforeTargetInspection?.Invoke(parent);
            }

            var result = HideTarget && matchesTarget ? null : inner.InspectChildNoFollow(parent, singleName);
            if (matchesTarget)
                AfterTargetInspection?.Invoke(result);
            return result;
        }

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
            => inner.ReadControlFile(file, maximumBytes);

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => inner.TryAcquireExclusiveLock(file);

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
        {
            var actual = ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
            return ChangeTargetParentNameProfile && inner.InspectHandle(parent).Identity == parentIdentity
                ? new PhysicalStoreNameSemantics(actual.ProfileId + "-changed", actual.Encoding,
                    actual.CaseSensitive, actual.NormalizationInsensitive)
                : actual;
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(
                parent, singleName, expectedFileIdentity);

        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity stagedIdentity, string destinationName, PhysicalFileIdentity? destinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(parent, stagedName,
                stagedIdentity, destinationName, destinationIdentity);

        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, name, identity);
    }
}
