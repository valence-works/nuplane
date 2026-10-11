using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageInstallIdentityReaderTests
{
    [SupportedPhysicalStoreFact]
    public void Observe_UsesHeldNativeDirectoriesAndEmptySingleLinkMarkerWithoutContentOrMutationIO()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var tree = CreateTree(context);
        var rootIdentity = new PhysicalRootIdentity(context.FileSystem.InspectHandle(context.Parent).Identity);
        var counters = new OperationCounters();
        var files = new CountingPhysicalStoreFileSystem(context.FileSystem, counters);
        var reader = new PackageInstallIdentityReader(files);

        using var observation = reader.Observe(
            context.Parent,
            rootIdentity,
            tree.RelativePath,
            tree.PackageId,
            tree.Version,
            alreadyVerifiedArchiveHash: "sha512:caller-verified");

        var install = observation.InstallIdentity;
        Assert.Equal(rootIdentity, install.Root);
        Assert.Equal(tree.RelativePath, install.RootRelativeInstallPath);
        Assert.Equal(tree.PackageId, install.PackageId);
        Assert.Equal(tree.Version, install.Version);
        Assert.Equal(tree.DirectoryIdentity, install.DirectoryIdentity);
        Assert.Equal(ProtectionDigest.PackageInstallCompletionIdentity(tree.MarkerIdentity!), install.CompletionIdentity);
        Assert.Equal("sha512:caller-verified", install.VerifiedArchiveHash);
        Assert.True(counters.DirectoryProfileObservations > 0);
        Assert.True(counters.ChildInspections > 0);
        Assert.True(counters.DirectoryOpens > 0);
        Assert.True(counters.FileOpens > 0);
        Assert.All(counters.OpenedFileNames, name => Assert.Equal(PackageInstallStore.CompletionMarkerFileName, name));
        Assert.Equal(0, counters.ControlReads);
        Assert.Equal(0, counters.Creates);
        Assert.Equal(0, counters.Writes);
        Assert.Equal(0, counters.Locks);
        Assert.Equal(0, counters.CanonicalNameObservations);

        observation.Revalidate();
        observation.Dispose();
        Assert.Throws<ObjectDisposedException>(observation.Revalidate);
        Assert.Equal(rootIdentity.HandleIdentity, context.FileSystem.InspectHandle(context.Parent).Identity);
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RejectsMissingNonemptyAndHardLinkedCompletionMarkers()
    {
        using (var missingContext = PhysicalStoreDirectoryPublicationTests.CreateContext())
        {
            var missing = CreateTree(missingContext, createMarker: false);
            AssertRefused(() => new PackageInstallIdentityReader(missingContext.FileSystem).Observe(
                missingContext.Parent, RootIdentity(missingContext), missing.RelativePath, missing.PackageId, missing.Version));
        }

        using (var nonemptyContext = PhysicalStoreDirectoryPublicationTests.CreateContext())
        {
            var nonempty = CreateTree(nonemptyContext, markerContents: "not empty");
            AssertRefused(() => new PackageInstallIdentityReader(nonemptyContext.FileSystem).Observe(
                nonemptyContext.Parent, RootIdentity(nonemptyContext), nonempty.RelativePath, nonempty.PackageId, nonempty.Version));
        }

        using (var hardlinkContext = PhysicalStoreDirectoryPublicationTests.CreateContext())
        {
            var hardlinked = CreateTree(hardlinkContext);
            CreateHardLink(hardlinked.MarkerPath, Path.Combine(Path.GetDirectoryName(hardlinked.MarkerPath)!, "marker-alias"));
            AssertRefused(() => new PackageInstallIdentityReader(hardlinkContext.FileSystem).Observe(
                hardlinkContext.Parent, RootIdentity(hardlinkContext), hardlinked.RelativePath, hardlinked.PackageId, hardlinked.Version));
        }
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RejectsLinkedDirectoryComponentsAndLinkedCompletionMarker()
    {
        using (var directoryContext = PhysicalStoreDirectoryPublicationTests.CreateContext())
        {
            var target = CreateTree(directoryContext);
            var linkedFeedPath = Path.Combine(directoryContext.ParentPath, "linked-feed");
            Directory.CreateSymbolicLink(linkedFeedPath, Path.Combine(directoryContext.ParentPath, "feed"));
            var linkedRelativePath = $"linked-feed/{target.PackageId}/{target.Version}";
            AssertRefused(() => new PackageInstallIdentityReader(directoryContext.FileSystem).Observe(
                directoryContext.Parent, RootIdentity(directoryContext), linkedRelativePath, target.PackageId, target.Version));
        }

        using (var markerContext = PhysicalStoreDirectoryPublicationTests.CreateContext())
        {
            var linkedMarker = CreateTree(markerContext, createMarker: false);
            var targetPath = Path.Combine(markerContext.ParentPath, "marker-target");
            File.WriteAllText(targetPath, "marker target");
            File.CreateSymbolicLink(linkedMarker.MarkerPath, targetPath);
            AssertRefused(() => new PackageInstallIdentityReader(markerContext.FileSystem).Observe(
                markerContext.Parent, RootIdentity(markerContext), linkedMarker.RelativePath, linkedMarker.PackageId, linkedMarker.Version));
        }
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RejectsWrongRootAndForeignFilesystemHandles()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var tree = CreateTree(context);
        var actualRoot = RootIdentity(context);
        var wrongRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            actualRoot.HandleIdentity.Provider,
            actualRoot.HandleIdentity.VolumeOrDeviceId,
            actualRoot.HandleIdentity.FileId + "-wrong"));
        var error = Assert.Throws<PackageStoreAdmissionException>(() => new PackageInstallIdentityReader(context.FileSystem).Observe(
            context.Parent, wrongRoot, tree.RelativePath, tree.PackageId, tree.Version));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, error.Reason);

        IPhysicalStoreFileSystem samePlatformForeignProvider = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var foreignProviderError = Assert.Throws<PackageStoreAdmissionException>(() =>
            new PackageInstallIdentityReader(samePlatformForeignProvider).Observe(
                context.Parent, actualRoot, tree.RelativePath, tree.PackageId, tree.Version));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignProviderError.Reason);

        IPhysicalStoreFileSystem unsupportedPlatformProvider = OperatingSystem.IsWindows()
            ? new UnixPhysicalStoreFileSystem()
            : new WindowsPhysicalStoreFileSystem();
        var platformError = Assert.Throws<PackageStoreAdmissionException>(() =>
            new PackageInstallIdentityReader(unsupportedPlatformProvider).Observe(
                context.Parent, actualRoot, tree.RelativePath, tree.PackageId, tree.Version));
        // Unix checks platform support first; Windows checks handle ownership before platform support.
        Assert.Equal(OperatingSystem.IsWindows()
            ? PackageStoreAdmissionReason.UnsupportedFilesystem
            : PackageStoreAdmissionReason.RootMismatch, platformError.Reason);
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RejectsRootLevelControlAndStagingDirectories()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var controlTree = CreateTree(context, feedName: RootMembershipRegistry.ControlDirectoryName);
        var stagingTree = CreateTree(context, feedName: ".tmp");
        var reader = new PackageInstallIdentityReader(context.FileSystem);
        var root = RootIdentity(context);

        AssertRefused(() => reader.Observe(context.Parent, root, controlTree.RelativePath, controlTree.PackageId, controlTree.Version));
        AssertRefused(() => reader.Observe(context.Parent, root, stagingTree.RelativePath, stagingTree.PackageId, stagingTree.Version));
    }

    [SupportedPhysicalStoreFact]
    public void Observe_RefusesAnUnknownNativeNameProfile()
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var tree = CreateTree(context);
        var counters = new OperationCounters
        {
            ProfileOverride = new PhysicalStoreNameSemantics(
                "ambiguous-profile",
                OperatingSystem.IsWindows() ? PhysicalStoreNameEncoding.Utf16LittleEndian : PhysicalStoreNameEncoding.Utf8,
                caseSensitive: false,
                normalizationInsensitive: false)
        };
        var files = new CountingPhysicalStoreFileSystem(context.FileSystem, counters);

        var error = Assert.Throws<PackageStoreAdmissionException>(() => new PackageInstallIdentityReader(files).Observe(
            context.Parent, RootIdentity(context), tree.RelativePath, tree.PackageId, tree.Version));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, error.Reason);
    }

    [LinuxExt4CasefoldFact]
    public void Observe_UsesNativeCasefoldProfileForReservedAliasesAndRetainsOrdinaryAliasSpelling()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext(enableCasefold: true);
        var reservedControl = CreateTree(context, feedName: RootMembershipRegistry.ControlDirectoryName);
        var reservedStaging = CreateTree(context, feedName: ".tmp");
        var ordinary = CreateTree(context, feedName: "feed");
        var reader = new PackageInstallIdentityReader(context.FileSystem);
        var root = RootIdentity(context);

        AssertRefused(() => reader.Observe(context.Parent, root,
            $".NUPLANE-STORE/{reservedControl.PackageId}/{reservedControl.Version}", reservedControl.PackageId, reservedControl.Version));
        AssertRefused(() => reader.Observe(context.Parent, root,
            $".TMP/{reservedStaging.PackageId}/{reservedStaging.Version}", reservedStaging.PackageId, reservedStaging.Version));

        using var observation = reader.Observe(context.Parent, root,
            $"FEED/{ordinary.PackageId}/{ordinary.Version}", ordinary.PackageId, ordinary.Version);
        Assert.Equal($"FEED/{ordinary.PackageId}/{ordinary.Version}", observation.InstallIdentity.RootRelativeInstallPath);
        Assert.Equal(ordinary.DirectoryIdentity, observation.InstallIdentity.DirectoryIdentity);
        observation.Revalidate();
    }

    [LinuxExt4CasefoldFact]
    public void Revalidate_RefusesAnInstallRenamedIntoAReservedNativeAlias()
    {
        using var context = UnixPhysicalStorePublicationTests.CreateContext(enableCasefold: true);
        var tree = CreateTree(context);
        var observation = new PackageInstallIdentityReader(context.FileSystem).Observe(
            context.Parent,
            RootIdentity(context),
            tree.RelativePath,
            tree.PackageId,
            tree.Version);
        try
        {
            Directory.Move(tree.DirectoryPath, Path.Combine(context.ParentPath, ".tmp"));

            AssertRefused(observation.Revalidate);
        }
        finally
        {
            observation.Dispose();
        }
    }

    [SupportedUnixFact]
    public void Revalidate_RefusesReplacedInstallDirectoryAndCompletionMarker()
    {
        using (var directoryContext = UnixPhysicalStorePublicationTests.CreateContext())
        {
            var tree = CreateTree(directoryContext);
            var observation = new PackageInstallIdentityReader(directoryContext.FileSystem).Observe(
                directoryContext.Parent, RootIdentity(directoryContext), tree.RelativePath, tree.PackageId, tree.Version);
            try
            {
                var movedDirectory = tree.DirectoryPath + "-moved";
                Directory.Move(tree.DirectoryPath, movedDirectory);
                Directory.CreateDirectory(tree.DirectoryPath);
                File.WriteAllBytes(Path.Combine(tree.DirectoryPath, PackageInstallStore.CompletionMarkerFileName), []);

                AssertRefused(observation.Revalidate);
            }
            finally
            {
                observation.Dispose();
            }
        }

        using (var markerContext = UnixPhysicalStorePublicationTests.CreateContext())
        {
            var tree = CreateTree(markerContext);
            var observation = new PackageInstallIdentityReader(markerContext.FileSystem).Observe(
                markerContext.Parent, RootIdentity(markerContext), tree.RelativePath, tree.PackageId, tree.Version);
            try
            {
                var replacement = tree.MarkerPath + ".replacement";
                File.WriteAllBytes(replacement, []);
                File.Move(replacement, tree.MarkerPath, overwrite: true);

                AssertRefused(observation.Revalidate);
            }
            finally
            {
                observation.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/feed/Demo.Package/1.2.3")]
    [InlineData("feed//Demo.Package/1.2.3")]
    [InlineData("feed/Demo.Package/../1.2.3")]
    [InlineData("feed/Different.Package/1.2.3")]
    [InlineData("feed/Demo.Package/2.0.0")]
    public void Observe_RejectsMalformedOrMismatchedPathHints(string relativePath)
    {
        using var context = PhysicalStoreDirectoryPublicationTests.CreateContext();
        var tree = CreateTree(context);
        Assert.Throws<ArgumentException>(() => new PackageInstallIdentityReader(context.FileSystem).Observe(
            context.Parent, RootIdentity(context), relativePath, tree.PackageId, tree.Version));
    }

    private static InstallTree CreateTree(
        PhysicalStorePublicationTestContext context,
        string feedName = "feed",
        string packageId = "Demo.Package",
        string version = "1.2.3",
        bool createMarker = true,
        string? markerContents = null)
    {
        var fileSystem = context.FileSystem;
        using var feed = fileSystem.CreateDirectoryExclusiveAt(context.Parent, feedName);
        using var package = fileSystem.CreateDirectoryExclusiveAt(feed, packageId);
        using var install = fileSystem.CreateDirectoryExclusiveAt(package, version);
        var directoryIdentity = fileSystem.InspectHandle(install).Identity;
        var markerPath = Path.Combine(context.ParentPath, feedName, packageId, version, PackageInstallStore.CompletionMarkerFileName);
        PhysicalFileIdentity? markerIdentity = null;
        if (createMarker)
        {
            if (markerContents is null)
            {
                using var marker = fileSystem.CreateFileExclusiveAt(install, PackageInstallStore.CompletionMarkerFileName);
                markerIdentity = fileSystem.InspectHandle(marker).Identity;
            }
            else
            {
                File.WriteAllText(markerPath, markerContents);
                markerIdentity = fileSystem.InspectChildNoFollow(install, PackageInstallStore.CompletionMarkerFileName)!.Identity;
            }
        }

        return new InstallTree(
            $"{feedName}/{packageId}/{version}",
            packageId,
            version,
            Path.Combine(context.ParentPath, feedName, packageId, version),
            markerPath,
            directoryIdentity,
            markerIdentity);
    }

    private static PhysicalRootIdentity RootIdentity(PhysicalStorePublicationTestContext context)
        => new(context.FileSystem.InspectHandle(context.Parent).Identity);

    private static void AssertRefused(Action action)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? WindowsLink(newPath, existingPath)
            : OperatingSystem.IsMacOS()
                ? DarwinLink(existingPath, newPath)
                : LinuxLink(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int DarwinLink(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int LinuxLink(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static int WindowsLink(string newPath, string existingPath)
        => CreateHardLinkW(newPath, existingPath, IntPtr.Zero) ? 0 : -1;

    private sealed record InstallTree(
        string RelativePath,
        string PackageId,
        string Version,
        string DirectoryPath,
        string MarkerPath,
        PhysicalFileIdentity DirectoryIdentity,
        PhysicalFileIdentity? MarkerIdentity);

    private sealed class OperationCounters
    {
        internal int ChildInspections;
        internal int DirectoryOpens;
        internal int FileOpens;
        internal List<string> OpenedFileNames { get; } = [];
        internal int DirectoryProfileObservations;
        internal int CanonicalNameObservations;
        internal int ControlReads;
        internal int Creates;
        internal int Writes;
        internal int Locks;
        internal PhysicalStoreNameSemantics? ProfileOverride;
    }

    private sealed class CountingPhysicalStoreFileSystem(
        IPhysicalStoreFileSystem inner,
        OperationCounters counters) : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem
    {
        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            counters.ChildInspections++;
            return inner.InspectChildNoFollow(parent, singleName);
        }

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            counters.DirectoryOpens++;
            return inner.OpenDirectoryChildNoFollow(parent, singleName);
        }

        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            counters.FileOpens++;
            counters.OpenedFileNames.Add(singleName);
            return inner.OpenFileChildNoFollow(parent, singleName, access);
        }

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);

        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);

        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            counters.Creates++;
            return inner.CreateDirectoryExclusiveAt(parent, singleName);
        }

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            counters.Creates++;
            return inner.CreateFileExclusiveAt(parent, singleName);
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            counters.ControlReads++;
            return inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            counters.Writes++;
            inner.WriteNewControlFile(file, contents);
        }

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            counters.Locks++;
            return inner.TryAcquireExclusiveLock(file);
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
        {
            counters.DirectoryProfileObservations++;
            return counters.ProfileOverride ?? ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
        {
            counters.CanonicalNameObservations++;
            return ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        }
    }
}
