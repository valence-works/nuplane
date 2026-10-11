using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageStoreAdmissionProspectiveRootTests
{
    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionAcceptsOnlyAnAbsentOrdinaryUnenrolledSuffix()
    {
        using var fixture = new PackageStoreFixture();
        var nativeFiles = CreateFileSystem();
        var observed = new SideEffectCountingFileSystem(nativeFiles);
        IPhysicalStoreFileSystem files = observed;
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var prospectiveRoot = Path.Combine(fixture.PackageInstallRoot, "first-run", "nested-root");
        var admission = new PackageStoreAdmission(files, registry, prospectiveRoot);

        await using (var result = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, result.Status);
            Assert.Null(result.Root);
            Assert.Null(result.Owner);
        }

        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "first-run")));
        Assert.False(Directory.Exists(prospectiveRoot));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync([prospectiveRoot], PackageStoreAdmissionKind.Installation));

        var missingArchive = Path.Combine(fixture.PackageInstallRoot, "missing.nupkg");
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireForInstallPathsAsync([missingArchive], PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "first-run")));
        Assert.False(File.Exists(missingArchive));

        foreach (var ambiguousSuffix in new[]
                 {
                     Path.Combine(fixture.PackageInstallRoot, "dot-missing", "."),
                     Path.Combine(fixture.PackageInstallRoot, "parent-missing", "..", "eventual-root")
                 })
        {
            var ambiguousAdmission = new PackageStoreAdmission(files, registry, ambiguousSuffix);
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await ambiguousAdmission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        }

        var alias = Path.Combine(fixture.RootPath, "packages-alias");
        Directory.CreateSymbolicLink(alias, fixture.PackageInstallRoot);
        var aliasedProspectiveRoot = Path.Combine(alias, "alias-first-run", "nested-root");
        var aliasAdmission = new PackageStoreAdmission(files, registry, aliasedProspectiveRoot);
        await using var aliased = await aliasAdmission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, aliased.Status);
        Assert.Null(aliased.Root);
        Assert.Null(aliased.Owner);
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "alias-first-run")));

        Assert.Equal(0, observed.ControlReadCount);
        Assert.Equal(0, observed.EntryCreateCount);
        Assert.Equal(0, observed.ControlWriteCount);
        Assert.Equal(0, observed.LockAttemptCount);
        Assert.NotEmpty(observed.OpenedHandles);
        Assert.All(observed.OpenedHandles, handle =>
            Assert.Throws<PackageStoreAdmissionException>(() => nativeFiles.InspectHandle(handle)));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesMissingSuffixBeneathObservedCompleteAuthority()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var prospectiveRoot = Path.Combine(context.Fixture.PackageInstallRoot, "missing-child", "nested-root");
        var admission = new PackageStoreAdmission(context.Files, context.Registry, prospectiveRoot);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(context.Fixture.PackageInstallRoot, "missing-child")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesReservedControlNameAsFirstMissingEdge()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var target = Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName);
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(target));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesReservedControlNameLaterInMissingSuffix()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var target = Path.Combine(fixture.PackageInstallRoot, "future-parent", RootMembershipRegistry.ControlDirectoryName);
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionUsesObservedNameProfileForReservedNameAlias()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var semantics = ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent);
        var target = Path.Combine(fixture.PackageInstallRoot, ".NUPLANE-STORE");
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        if (semantics.CaseSensitive)
        {
            await using var result = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, result.Status);
            Assert.Null(result.Root);
        }
        else
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        }

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName)));
    }

    [LinuxExt4CasefoldFact]
    public async Task ConfiguredRootAdmissionRefusesExt4CasefoldAliasOfReservedControlName()
    {
        using var fixture = new PackageStoreFixture();
        var files = new UnixPhysicalStoreFileSystem();
        UnixPhysicalStoreIdentityTests.EnableExt4Casefold(fixture.PackageInstallRoot);
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var semantics = ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent);
        Assert.Equal("linux-ext4-casefold-v1", semantics.ProfileId);
        Assert.False(semantics.CaseSensitive);

        var target = Path.Combine(fixture.PackageInstallRoot, ".nuplane-\u017Ftore");
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionDoesNotBroadenTheReservedNameMatchToPrefixes()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var target = Path.Combine(fixture.PackageInstallRoot, ".nuplane-store-backup");
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        await using var result = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, result.Status);
        Assert.Null(result.Root);
        Assert.False(Directory.Exists(target));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesUnknownNativeNameProfileForMissingSuffix()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var unknownProfileFiles = new UnknownNameProfileFileSystem(files);
        var target = Path.Combine(fixture.PackageInstallRoot, ".nuplane-\u017Ftore");
        var admission = new PackageStoreAdmission(unknownProfileFiles,
            new RootMembershipRegistry(unknownProfileFiles, new StoreStateSerializer()), target);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        Assert.False(Directory.Exists(target));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionDoesNotGuessUnicodeReservedNameEquivalence()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        using var parent = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var semantics = ((IPhysicalStoreNameFileSystem)files).ObserveDirectoryNameSemantics(parent);
        var target = Path.Combine(fixture.PackageInstallRoot, ".nuplane-\u017Ftore");
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        if (semantics.CaseSensitive && !semantics.NormalizationInsensitive)
        {
            await using var result = await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation);
            Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, result.Status);
            Assert.Null(result.Root);
        }
        else
        {
            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        }

        Assert.False(Directory.Exists(target));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesNonAsciiLaterSuffixWithoutItsNativeProfile()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var target = Path.Combine(fixture.PackageInstallRoot, "future-parent", "caf\u00E9-root");
        var admission = new PackageStoreAdmission(files, new RootMembershipRegistry(files, new StoreStateSerializer()), target);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
        Assert.False(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, "future-parent")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesAliasEscapeFromCompleteAuthorityBeforeMissingSuffix()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var outside = context.Fixture.CreateDirectory("outside-root");
        var alias = Path.Combine(context.Fixture.PackageInstallRoot, "outside-alias");
        Directory.CreateSymbolicLink(alias, outside);
        var target = Path.Combine(alias, "missing-child", "nested-root");
        var admission = new PackageStoreAdmission(context.Files, context.Registry, target);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(outside, "missing-child")));
    }

    [SupportedPhysicalStoreFact]
    public async Task ConfiguredRootAdmissionRefusesMissingTargetInsideAnUnresolvedAliasExpansion()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateFileSystem();
        var outside = fixture.CreateDirectory("outside-target");
        var alias = Path.Combine(fixture.PackageInstallRoot, "store-alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(outside, "missing", "nested-root"));
        var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
        var admission = new PackageStoreAdmission(files, registry, alias);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));
        Assert.False(Directory.Exists(Path.Combine(outside, "missing")));
    }

    [SupportedPhysicalStoreFact]
    public Task ConfiguredRootAdmissionRevalidatesMissingEdgeDuringInitialResolutionReplay()
        => AssertMissingEdgeRaceFails(createOnObservation: 4);

    [SupportedPhysicalStoreFact]
    public Task ConfiguredRootAdmissionRevalidatesMissingEdgeBeforeAdmissionReturns()
        => AssertMissingEdgeRaceFails(createOnObservation: 5);

    private static async Task AssertMissingEdgeRaceFails(int createOnObservation)
    {
        using var fixture = new PackageStoreFixture();
        var nativeFiles = CreateFileSystem();
        using var parent = PhysicalStoreTestDirectory.Open(nativeFiles, fixture.PackageInstallRoot);
        var parentIdentity = nativeFiles.InspectHandle(parent).Identity;
        const string firstMissingName = "racing-root";
        var racingFiles = new MissingEdgeRaceFileSystem(nativeFiles, parentIdentity,
            firstMissingName, fixture.PackageInstallRoot, createOnObservation);
        var registry = new RootMembershipRegistry(racingFiles, new StoreStateSerializer());
        var target = Path.Combine(fixture.PackageInstallRoot, firstMissingName, "nested-root");
        var admission = new PackageStoreAdmission(racingFiles, registry, target);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Installation));

        Assert.True(racingFiles.MutationPerformed);
        Assert.True(Directory.Exists(Path.Combine(fixture.PackageInstallRoot, firstMissingName)));
        Assert.False(Directory.Exists(target));
        Assert.NotEmpty(racingFiles.OpenedHandles);
        Assert.All(racingFiles.OpenedHandles, handle =>
            Assert.Throws<PackageStoreAdmissionException>(() => nativeFiles.InspectHandle(handle)));
    }

    private static IPhysicalStoreFileSystem CreateFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private abstract class DelegatingNativeFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem,
            IPhysicalStorePublicationFileSystem, IPhysicalStoreDirectoryPublicationFileSystem
    {
        protected IPhysicalStoreFileSystem Inner { get; } = inner;
        internal List<PhysicalStoreHandle> OpenedHandles { get; } = [];

        public virtual PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor)
            => Track(Inner.OpenNamespaceRoot(anchor));

        public virtual PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Inner.InspectChildNoFollow(parent, singleName);

        public virtual PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(Inner.OpenDirectoryChildNoFollow(parent, singleName));

        public virtual PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => Track(Inner.OpenParentDirectory(directory));

        public virtual PhysicalStoreFileHandle OpenFileChildNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            FileAccess access)
            => Track(Inner.OpenFileChildNoFollow(parent, singleName, access));

        public virtual string ReadLinkTargetNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedLinkIdentity)
            => Inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);

        public virtual PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => Inner.InspectHandle(handle);

        public virtual PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(Inner.CreateDirectoryExclusiveAt(parent, singleName));

        public virtual PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => Track(Inner.CreateFileExclusiveAt(parent, singleName));

        public virtual byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => Inner.ReadControlFile(file, maximumBytes);

        public virtual void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => Inner.WriteNewControlFile(file, contents);

        public virtual ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => Inner.TryAcquireExclusiveLock(file);

        public virtual PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)Inner).ObserveDirectoryNameSemantics(parent);

        public virtual PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)Inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);

        public virtual PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName,
            PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)Inner).PublishControlFileAt(
                parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);

        public virtual void RemoveControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)Inner).RemoveControlFileAt(parent, singleName, expectedIdentity);

        public virtual PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)Inner).ObserveCanonicalDirectoryNameNoFollow(
                parent, singleName, expectedDirectoryIdentity);

        public virtual PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)Inner).PublishDirectoryNoReplaceAt(
                parent, stagedName, expectedStagedIdentity, destinationName);

        protected THandle Track<THandle>(THandle handle) where THandle : PhysicalStoreHandle
        {
            OpenedHandles.Add(handle);
            return handle;
        }
    }

    private sealed class SideEffectCountingFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingNativeFileSystem(inner)
    {
        internal int ControlReadCount { get; private set; }
        internal int EntryCreateCount { get; private set; }
        internal int ControlWriteCount { get; private set; }
        internal int LockAttemptCount { get; private set; }

        public override PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            EntryCreateCount++;
            return base.CreateDirectoryExclusiveAt(parent, singleName);
        }

        public override PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            EntryCreateCount++;
            return base.CreateFileExclusiveAt(parent, singleName);
        }

        public override byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            ControlReadCount++;
            return base.ReadControlFile(file, maximumBytes);
        }

        public override void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            ControlWriteCount++;
            base.WriteNewControlFile(file, contents);
        }

        public override ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            LockAttemptCount++;
            return base.TryAcquireExclusiveLock(file);
        }
    }

    private sealed class UnknownNameProfileFileSystem(IPhysicalStoreFileSystem inner)
        : DelegatingNativeFileSystem(inner)
    {
        public override PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => new("unqualified-name-profile",
                OperatingSystem.IsWindows() ? PhysicalStoreNameEncoding.Utf16LittleEndian : PhysicalStoreNameEncoding.Utf8,
                caseSensitive: false,
                normalizationInsensitive: false);
    }

    private sealed class MissingEdgeRaceFileSystem(
        IPhysicalStoreFileSystem inner,
        PhysicalFileIdentity expectedParentIdentity,
        string firstMissingName,
        string parentPath,
        int createOnObservation)
        : DelegatingNativeFileSystem(inner)
    {
        private int _matchingObservations;

        internal bool MutationPerformed { get; private set; }

        public override PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            if (!MutationPerformed && string.Equals(singleName, firstMissingName, StringComparison.Ordinal) &&
                Inner.InspectHandle(parent).Identity == expectedParentIdentity &&
                ++_matchingObservations == createOnObservation)
            {
                Directory.CreateDirectory(Path.Combine(parentPath, firstMissingName));
                MutationPerformed = true;
            }

            return base.InspectChildNoFollow(parent, singleName);
        }
    }
}
