using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class NativePackageGraphFileReaderTests
{
    private const string Nuspec = """<package><metadata><dependencies><dependency id="Another.Dependency" version="[1.0.0]" /></dependencies></metadata></package>""";

    [SupportedPhysicalStoreFact]
    public void UnenrolledRead_CopiesNuspecAndSortedNestedAssetNames()
    {
        using var fixture = new PackageStoreFixture();
        var install = fixture.CreateDirectory("packages/feed/Example/1.0.0");
        WritePackageInputs(install);

        var result = new NativePackageGraphFileReader().ReadInstallFiles(Package(install));

        Assert.Equal("Another.Dependency", Assert.Single(result.Nuspec!.Descendants("dependency")).Attribute("id")!.Value);
        Assert.Equal(new[] { Path.Combine("lib", "A.dll"), Path.Combine("lib", "Z.dll") }, result.RuntimeAssets);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.RuntimeAssets)[0] = "changed");
        Assert.Same(InstalledPackageGraphFiles.Empty,
            new NativePackageGraphFileReader().ReadInstallFiles(Package(fixture.GetPath("packages/feed/Missing/1.0.0"))));
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_UsesOriginalNativeProviderWhileEveryOperationLockIsHeld()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        fixture.Files.OnNuspecRead = fixture.AssertAllLocksBusy;

        var result = fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath));

        Assert.NotNull(result.Nuspec);
        Assert.Equal(1, fixture.Files.NuspecReadCount);
        Assert.Equal(2, result.RuntimeAssets.Count);
    }

    [SupportedPhysicalStoreFact]
    public async Task UnscopedRead_RefusesEnrolledPackage()
    {
        await using var fixture = await ReaderFixture.CreateAsync();

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            new NativePackageGraphFileReader().ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesWrongRootAndExpiredBorrowBeforeNuspecRead()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        using var other = new PackageStoreFixture();
        var install = other.CreateDirectory("packages/feed/Other/1.0.0");
        WritePackageInputs(install);
        var wrong = Assert.Throws<PackageStoreAdmissionException>(() => fixture.Reader.ReadInstallFiles(Package(install)));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, wrong.Reason);

        fixture.Borrow.Dispose();
        var expired = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        Assert.Equal(0, fixture.Files.NuspecReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesLinkedAssetDirectoryWithoutFollowingIt()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        var target = fixture.Context.Fixture.CreateDirectory("outside-assets");
        File.WriteAllText(Path.Combine(target, "Outside.dll"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Context.SharedInstallPath, "linked-assets"), target);

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_RefusesNestedAuthorityBeforeReadingItsPayload()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        var lib = Path.Combine(fixture.Context.SharedInstallPath, "lib");
        Directory.CreateDirectory(Path.Combine(lib, RootMembershipRegistry.ControlDirectoryName));

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(0, fixture.Files.NuspecReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_ReplaysNestedAuthorityAbsenceAfterLaterNuspecRead()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        fixture.Files.OnNuspecRead = () => Directory.CreateDirectory(Path.Combine(
            fixture.Context.SharedInstallPath, "lib", RootMembershipRegistry.ControlDirectoryName));

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(1, fixture.Files.NuspecReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedRead_ReplaysEarlierAssetIdentityAfterLaterNuspecRead()
    {
        await using var fixture = await ReaderFixture.CreateAsync();
        fixture.Files.OnNuspecRead = () =>
        {
            var path = Path.Combine(fixture.Context.SharedInstallPath, "lib", "A.dll");
            // Retain the old object under another name so native identity cannot be recycled.
            File.Move(path, fixture.Context.Fixture.GetPath("retained-old-asset"));
            File.WriteAllText(path, "a");
        };

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            fixture.Reader.ReadInstallFiles(Package(fixture.Context.SharedInstallPath)));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(1, fixture.Files.NuspecReadCount);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledRead_RefusesOversizedNuspecBeforeXmlParsing()
    {
        using var fixture = new PackageStoreFixture();
        var install = fixture.CreateDirectory("packages/feed/Example/1.0.0");
        using (var file = File.Create(Path.Combine(install, "z.nuspec")))
            file.SetLength(NativePackageGraphFileReader.MaximumNuspecBytes + 1L);

        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            new NativePackageGraphFileReader().ReadInstallFiles(Package(install)));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    [SupportedPhysicalStoreFact]
    public void UnenrolledRead_RefusesNuspecDtdAndExcessiveDepth()
    {
        using var fixture = new PackageStoreFixture();
        var install = fixture.CreateDirectory("packages/feed/Example/1.0.0");
        File.WriteAllText(Path.Combine(install, "z.nuspec"), "<!DOCTYPE package [<!ENTITY x 'expanded'>]><package>&x;</package>");
        Assert.Throws<System.Xml.XmlException>(() => new NativePackageGraphFileReader().ReadInstallFiles(Package(install)));
        File.Delete(Path.Combine(install, "z.nuspec"));
        var nested = install;
        for (var i = 0; i <= NativePackageGraphFileReader.MaximumDepth; i++)
            nested = Directory.CreateDirectory(Path.Combine(nested, "d")).FullName;
        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            new NativePackageGraphFileReader().ReadInstallFiles(Package(install)));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    private static ResolvedPackage Package(string installPath)
        => new("Shared.Dependency", "2.1.0", "feed", installPath, DateTimeOffset.UnixEpoch, "test");

    private static void WritePackageInputs(string install)
    {
        var lib = Directory.CreateDirectory(Path.Combine(install, "lib")).FullName;
        File.WriteAllText(Path.Combine(lib, "Z.dll"), "z");
        File.WriteAllText(Path.Combine(lib, "A.dll"), "a");
        // Replace the shared enrollment fixture's nuspec so this test has one selected payload.
        File.Delete(Path.Combine(install, "Shared.Dependency.nuspec"));
        // Sort after lib so the mutation case checks replay of an earlier subtree.
        File.WriteAllText(Path.Combine(install, "z.nuspec"), Nuspec);
    }

    private sealed class ReaderFixture : IAsyncDisposable
    {
        internal RootMembershipProtectionVerificationTests.Context Context { get; private set; } = null!;
        internal TrackingFileSystem Files { get; private set; } = null!;
        internal PackageStoreOperationBorrow Borrow { get; private set; } = null!;
        internal NativePackageGraphFileReader Reader { get; private set; } = null!;
        private PackageStoreRootOperationAdmission? _admission;

        internal static async Task<ReaderFixture> CreateAsync()
        {
            var result = new ReaderFixture();
            try
            {
                result.Context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
                WritePackageInputs(result.Context.SharedInstallPath);
                using var directory = PhysicalStoreTestDirectory.Open(result.Context.Files, result.Context.SharedInstallPath);
                var identity = result.Context.Files.InspectChildNoFollow(directory, "z.nuspec")!.Identity;
                result.Files = new TrackingFileSystem(result.Context.Files, identity);
                var registry = new RootMembershipRegistry(result.Files, new StoreStateSerializer());
                result._admission = await new PackageStoreAdmission(result.Files, registry, result.Context.Fixture.PackageInstallRoot)
                    .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
                result.Borrow = Assert.IsType<PackageStoreOperationOwner>(result._admission.Owner).Borrow();
                result.Reader = new NativePackageGraphFileReader(result.Borrow);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        internal void AssertAllLocksBusy()
        {
            var ledger = Context.Registry.ReadCandidate(Context.Root);
            var lockNames = new[] { "root.lock" }.Concat(ledger.Members.Select(member =>
                PhysicalStoreLock.GetMemberLockName(Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding).StateSlot)));
            using var control = Context.Files.OpenDirectoryChildNoFollow(Context.Root, RootMembershipRegistry.ControlDirectoryName);
            foreach (var name in lockNames)
            {
                using var file = Context.Files.OpenFileChildNoFollow(control, name, FileAccess.ReadWrite);
                var owner = Context.Files.TryAcquireExclusiveLock(file).GetAwaiter().GetResult();
                if (owner is null)
                    continue;
                owner.DisposeAsync().GetAwaiter().GetResult();
                Assert.Fail($"Native operation lock '{name}' was released during nuspec byte IO.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Borrow?.Dispose();
            try { if (_admission is not null) await _admission.DisposeAsync(); }
            finally { Context?.Dispose(); }
        }
    }

    private sealed class TrackingFileSystem(IPhysicalStoreFileSystem inner, PhysicalFileIdentity nuspecIdentity)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStoreDirectoryEnumerationFileSystem,
            IPhysicalStorePublicationFileSystem
    {
        internal Action? OnNuspecRead { get; set; }
        internal int NuspecReadCount { get; private set; }
        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string name) => inner.InspectChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string name) => inner.OpenDirectoryChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory) => inner.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string name, FileAccess access) => inner.OpenFileChildNoFollow(parent, name, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expectedIdentity) => inner.ReadLinkTargetNoFollow(parent, name, expectedIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string name) => inner.CreateDirectoryExclusiveAt(parent, name);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string name) => inner.CreateFileExclusiveAt(parent, name);
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            if (inner.InspectHandle(file).Identity == nuspecIdentity)
            {
                NuspecReadCount++;
                OnNuspecRead?.Invoke();
            }
            return inner.ReadControlFile(file, maximumBytes);
        }
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents) => inner.WriteNewControlFile(file, contents);
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file) => inner.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, name, identity);
        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => ((IPhysicalStoreDirectoryEnumerationFileSystem)inner).EnumerateChildNamesNoFollow(parent, maximumEntries);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity stagedIdentity, string destinationName, PhysicalFileIdentity? destinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(parent, stagedName,
                stagedIdentity, destinationName, destinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity identity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, name, identity);
    }
}
