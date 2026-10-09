using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAdmissionRefusalTests
{
    [SupportedPhysicalStoreFact]
    public async Task BothAdmissionPathsRefuseDigestValidSemanticallyInvalidCompleteStateAndUnwind()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var state = context.States["second"];
        var versions = new Dictionary<string, string>(state.ActiveVersionById, StringComparer.OrdinalIgnoreCase)
        {
            ["Root.Second"] = "9.0.0"
        };
        var invalidBody = state with
        {
            ActiveVersionById = versions,
            LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase)
        };
        var invalidState = ReprotectNext(context, invalidBody);

        await context.Registry.WithCompleteMemberLocationsAsync(context.Root, context.RootIdentity, 1,
            async (locked, token) =>
            {
                await locked.PublishStateAsync("second", invalidState, token);
                return true;
            }, CancellationToken.None);

        await AssertBothAdmissionPathsRefuseAndUnwindAsync(context, context.States["first"]
            .ActivePackageDescriptorsByIdNormalized["Root.First"].InstallPath);
    }

    [SupportedPhysicalStoreFact]
    public async Task BothAdmissionPathsRefuseReplacedNativeCompletionMarkerIdentityAndUnwind()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        ReplaceCompletionMarker(context);

        await AssertBothAdmissionPathsRefuseAndUnwindAsync(context, context.SharedInstallPath);
    }

    [SupportedPhysicalStoreFact]
    public async Task BothAdmissionPathsRefuseReplacedNativeInstallDirectoryIdentityAndUnwind()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        ReplaceInstallDirectory(context);

        await AssertBothAdmissionPathsRefuseAndUnwindAsync(context, context.SharedInstallPath);
    }

    private static async Task AssertBothAdmissionPathsRefuseAndUnwindAsync(
        RootMembershipProtectionVerificationTests.Context context,
        string installPath)
    {
        var files = new TrackingFileSystem(context.Files);
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, context.Fixture.PackageInstallRoot);

        PackageStoreRootOperationAdmission? unexpectedRootAdmission = null;
        var rootError = await Record.ExceptionAsync(async () =>
            unexpectedRootAdmission = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading));
        if (unexpectedRootAdmission is not null)
            await unexpectedRootAdmission.DisposeAsync();
        files.AssertAllOpenedHandlesClosed();
        await AssertNativeLockSetCanBeReacquiredAsync(context);

        PackageStorePathAdmission? unexpectedPathAdmission = null;
        var pathError = await Record.ExceptionAsync(async () =>
            unexpectedPathAdmission = await admission.AcquireForInstallPathsAsync(
                [installPath], PackageStoreAdmissionKind.Loading));
        if (unexpectedPathAdmission is not null)
            await unexpectedPathAdmission.DisposeAsync();
        files.AssertAllOpenedHandlesClosed();
        await AssertNativeLockSetCanBeReacquiredAsync(context);

        Assert.IsType<PackageStoreAdmissionException>(rootError);
        Assert.IsType<PackageStoreAdmissionException>(pathError);
    }

    private static Task AssertNativeLockSetCanBeReacquiredAsync(
        RootMembershipProtectionVerificationTests.Context context)
        => context.Registry.WithCompleteMemberLocationsAsync(context.Root, context.RootIdentity, 1,
            (_, _) => Task.FromResult(true), CancellationToken.None);

    private static StoreStateRecord ReprotectNext(
        RootMembershipProtectionVerificationTests.Context context,
        StoreStateRecord state)
    {
        var old = state.ProtectionRecord!;
        var candidate = new PackageProtectionRecord(1, context.RootIdentity, 1, old.MemberId,
            old.Revision + 1, ProtectionDigest.StateBody(state), new string('0', 64),
            old.ActiveClosure, old.RecoverableClosure, [], legacyUnknownRecovery: false);
        var protection = new PackageProtectionRecord(1, context.RootIdentity, 1, old.MemberId,
            old.Revision + 1, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate),
            old.ActiveClosure, old.RecoverableClosure, [], legacyUnknownRecovery: false);
        return state with { ProtectionRecord = protection };
    }

    private static void ReplaceCompletionMarker(RootMembershipProtectionVerificationTests.Context context)
    {
        var markerName = PackageInstallStore.CompletionMarkerFileName;
        var markerPath = Path.Combine(context.SharedInstallPath, markerName);
        var replacementPath = markerPath + ".replacement";
        using var parent = PhysicalStoreTestDirectory.Open(context.Files, context.SharedInstallPath);
        var original = context.Files.InspectChildNoFollow(parent, markerName)
            ?? throw new InvalidOperationException("The completed install marker is missing.");
        File.WriteAllBytes(replacementPath, []);
        var replacement = context.Files.InspectChildNoFollow(parent, Path.GetFileName(replacementPath))
            ?? throw new InvalidOperationException("The replacement install marker is missing.");
        Assert.NotEqual(original.Identity, replacement.Identity);

        File.Move(replacementPath, markerPath, overwrite: true);
        var actual = context.Files.InspectChildNoFollow(parent, markerName)
            ?? throw new InvalidOperationException("The replacement install marker disappeared.");
        Assert.Equal(replacement.Identity, actual.Identity);
    }

    private static void ReplaceInstallDirectory(RootMembershipProtectionVerificationTests.Context context)
    {
        var path = context.SharedInstallPath;
        var parentPath = Path.GetDirectoryName(path)!;
        var basename = Path.GetFileName(path);
        var backupPath = Path.Combine(parentPath, basename + ".original-" + Guid.NewGuid().ToString("N"));
        using var parent = PhysicalStoreTestDirectory.Open(context.Files, parentPath);
        var original = context.Files.InspectChildNoFollow(parent, basename)
            ?? throw new InvalidOperationException("The protected install directory is missing.");

        Directory.Move(path, backupPath);
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(backupPath))
            File.Copy(file, Path.Combine(path, Path.GetFileName(file)));

        var replacement = context.Files.InspectChildNoFollow(parent, basename)
            ?? throw new InvalidOperationException("The replacement install directory is missing.");
        Assert.NotEqual(original.Identity, replacement.Identity);
    }

    private sealed class TrackingFileSystem : IPhysicalStoreFileSystem,
        IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _inner;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStorePublicationFileSystem _publication;
        private readonly object _gate = new();
        private readonly List<PhysicalStoreHandle> _opened = [];

        internal TrackingFileSystem(IPhysicalStoreFileSystem inner)
        {
            _inner = inner;
            _names = inner as IPhysicalStoreNameFileSystem
                ?? throw new InvalidOperationException("The test filesystem has no native-name contract.");
            _publication = inner as IPhysicalStorePublicationFileSystem
                ?? throw new InvalidOperationException("The test filesystem has no publication contract.");
        }

        internal void AssertAllOpenedHandlesClosed()
        {
            PhysicalStoreHandle[] handles;
            lock (_gate)
                handles = _opened.ToArray();
            Assert.NotEmpty(handles);
            foreach (var handle in handles)
            {
                var error = Record.Exception(() => _inner.InspectHandle(handle));
                var refusal = Assert.IsType<PackageStoreAdmissionException>(error);
                Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, refusal.Reason);
            }
        }

        private T Track<T>(T handle) where T : PhysicalStoreHandle
        {
            lock (_gate)
                _opened.Add(handle);
            return handle;
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
            => Track(_inner.OpenNamespaceRoot(anchor));
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(_inner.OpenDirectoryChildNoFollow(parent, singleName));
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => Track(_inner.OpenParentDirectory(directory));
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => Track(_inner.OpenFileChildNoFollow(parent, singleName, access));
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => _inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
            => _inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(_inner.CreateDirectoryExclusiveAt(parent, singleName));
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(_inner.CreateFileExclusiveAt(parent, singleName));
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => _inner.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => _inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => _inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent, string stagedName, PhysicalFileIdentity expectedStagedIdentity,
            string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity,
                destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);
    }
}
