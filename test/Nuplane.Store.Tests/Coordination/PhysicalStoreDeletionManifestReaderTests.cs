using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreDeletionManifestReaderTests
{
    [SupportedPhysicalStoreFact]
    public void Capture_RecordsNestedCanonicalEntriesWithoutMutatingContents()
    {
        using var context = new ManifestContext();
        context.WritePayload("nested/payload.bin", "unchanged");
        context.WritePayload("sibling.bin", "sibling");

        var manifest = context.Capture();

        Assert.Equal(context.Install.DirectoryIdentity, manifest.InstallDirectoryIdentity);
        Assert.Equal(new[] { ".nuplane-ready", "nested", "nested/payload.bin", "sibling.bin" },
            manifest.Entries.Select(PathOf));
        using var nested = context.Native.FileSystem.OpenDirectoryChildNoFollow(context.InstallDirectory, "nested");
        foreach (var entry in manifest.Entries)
        {
            Assert.Equal(entry.Identity,
                context.Native.FileSystem.InspectChildNoFollow(
                    entry.RelativeComponents.Length == 1 ? context.InstallDirectory : nested,
                    entry.RelativeComponents[^1])!.Identity);
        }
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(context.InstallPath, "nested", "payload.bin")));
        Assert.Equal("sibling", File.ReadAllText(Path.Combine(context.InstallPath, "sibling.bin")));
        Assert.Empty(File.ReadAllBytes(context.MarkerPath));

    }

    [SupportedPhysicalStoreFact]
    public void Capture_SameIdentityQuarantineRetainsTheExactBaseline()
    {
        using var context = new ManifestContext();
        context.WritePayload("payload.bin", "keep");
        var baseline = context.Capture();
        context.MoveToQuarantine();

        var quarantined = context.Capture();

        Assert.Equal(0, baseline.RequireExactSubset(quarantined));
        Assert.Equal(0, quarantined.RequireExactSubset(baseline));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.InstallPath, "payload.bin")));
    }

    [SupportedPhysicalStoreFact]
    public void Subset_ReportsActuallyMissingPayloadAndEmptyDirectoryWithMarkerIntact()
    {
        using var context = new ManifestContext();
        context.WritePayload("nested/payload.bin", "remove");
        context.WritePayload("keep.bin", "keep");
        var baseline = context.Capture();
        var directory = baseline.Entries.Single(entry => PathOf(entry) == "nested");
        var payload = baseline.Entries.Single(entry => PathOf(entry) == "nested/payload.bin");
        var deletion = (IPhysicalStoreDeletionFileSystem)context.Native.FileSystem;
        using (var nested = context.Native.FileSystem.OpenDirectoryChildNoFollow(context.InstallDirectory, "nested"))
            deletion.RemoveFileAt(nested, "payload.bin", payload.Identity);
        deletion.RemoveEmptyDirectoryAt(context.InstallDirectory, "nested", directory.Identity);

        var remaining = context.Capture();

        Assert.Equal(2, baseline.RequireExactSubset(remaining));
        Assert.Equal(new[] { ".nuplane-ready", "keep.bin" }, remaining.Entries.Select(PathOf));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.InstallPath, "keep.bin")));
        Assert.Empty(File.ReadAllBytes(context.MarkerPath));
    }

    [SupportedPhysicalStoreFact]
    public void Subset_RefusesAddedNamesAndReplacedNativeIdentities()
    {
        foreach (var replace in new[] { false, true })
        {
            using var context = new ManifestContext();
            context.WritePayload("payload.bin", "original");
            var baseline = context.Capture();
            if (replace)
                File.Move(Path.Combine(context.InstallPath, "payload.bin"), Path.Combine(context.Native.ParentPath, "saved.bin"));
            context.WritePayload(replace ? "payload.bin" : "added.bin", "new");
            var remaining = context.Capture();

            AssertRefused(() => baseline.RequireExactSubset(remaining));

            Assert.Equal("new", File.ReadAllText(Path.Combine(context.InstallPath, replace ? "payload.bin" : "added.bin")));
            if (replace)
                Assert.Equal("original", File.ReadAllText(Path.Combine(context.Native.ParentPath, "saved.bin")));
        }
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesFileAndDirectorySymlinksWithoutTouchingTargets()
    {
        foreach (var directory in new[] { false, true })
        {
            using var context = new ManifestContext();
            var target = Path.Combine(context.Native.ParentPath, "target");
            var link = Path.Combine(context.InstallPath, "link");
            if (directory)
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "payload"), "outside-install");
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.WriteAllText(target, "outside-install");
                File.CreateSymbolicLink(link, target);
            }

            AssertRefused(() => context.Capture());

            Assert.Equal("outside-install", File.ReadAllText(directory ? Path.Combine(target, "payload") : target));
        }
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesHardLinkedPayloadAndPreservesBothNames()
    {
        using var context = new ManifestContext();
        context.WritePayload("payload.bin", "keep");
        var payload = Path.Combine(context.InstallPath, "payload.bin");
        var alias = Path.Combine(context.Native.ParentPath, "alias.bin");
        if (OperatingSystem.IsWindows())
            WindowsPhysicalStorePublicationTests.CreateHardLink(payload, alias);
        else
            UnixPhysicalStorePublicationTests.CreateHardLink(payload, alias);

        AssertRefused(() => context.Capture());

        Assert.Equal("keep", File.ReadAllText(payload));
        Assert.Equal("keep", File.ReadAllText(alias));
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesMissingNonemptyAndNonFileMarkers()
    {
        foreach (var kind in new[] { "missing", "nonempty", "directory", "link" })
        {
            using var context = new ManifestContext();
            File.Delete(context.MarkerPath);
            if (kind == "nonempty")
                File.WriteAllText(context.MarkerPath, "invalid");
            else if (kind == "directory")
                Directory.CreateDirectory(context.MarkerPath);
            else if (kind == "link")
            {
                var target = Path.Combine(context.Native.ParentPath, "marker-target");
                File.WriteAllBytes(target, []);
                File.CreateSymbolicLink(context.MarkerPath, target);
            }

            AssertRefused(() => context.Capture());
        }
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesMismatchedInstallAndCompletionIdentity()
    {
        using var context = new ManifestContext();
        var actual = context.Install.DirectoryIdentity;
        var wrong = new PhysicalFileIdentity(actual.Provider, actual.VolumeOrDeviceId, actual.FileId + "-wrong");

        AssertRefused(() => context.Reader.Capture(context.InstallDirectory,
            context.Identity(directory: wrong), CancellationToken.None));
        AssertRefused(() => context.Reader.Capture(context.InstallDirectory,
            context.Identity(completion: new string('0', 64)), CancellationToken.None));

        // Keep the original native object alive, so replacement cannot reuse its identity.
        var savedMarker = Path.Combine(context.Native.ParentPath, "original-marker");
        File.Move(context.MarkerPath, savedMarker);
        File.WriteAllBytes(context.MarkerPath, []);
        AssertRefused(() => context.Capture());

        Assert.Empty(File.ReadAllBytes(savedMarker));
        Assert.Empty(File.ReadAllBytes(context.MarkerPath));
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesAStoreRootOnAnotherNativeVolume()
    {
        using var context = new ManifestContext();
        var actual = context.Install.Root.HandleIdentity;
        var wrong = new PhysicalRootIdentity(new PhysicalFileIdentity(actual.Provider, actual.VolumeOrDeviceId + "-other", actual.FileId));

        AssertRefused(() => context.Reader.Capture(context.InstallDirectory,
            context.Identity(root: wrong), CancellationToken.None));

        Assert.Empty(File.ReadAllBytes(context.MarkerPath));
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesForeignAndDisposedHandles()
    {
        using var context = new ManifestContext();
        using var foreign = new ManifestContext();
        AssertRefused(() => foreign.Reader.Capture(context.InstallDirectory, context.Install, CancellationToken.None),
            PackageStoreAdmissionReason.RootMismatch);
        context.InstallDirectory.Dispose();

        AssertRefused(() => context.Capture(), PackageStoreAdmissionReason.ExpiredScope);
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesAnOversizedDirectoryWithoutTruncation()
    {
        using var context = new ManifestContext();
        for (var index = 0; index < PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory; index++)
            context.WritePayload($"entry-{index:D4}", "");

        AssertRefused(() => context.Capture());

        Assert.Equal(PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory + 1,
            Directory.GetFileSystemEntries(context.InstallPath).Length);
    }

    [SupportedPhysicalStoreFact]
    public void Capture_RefusesExcessiveDepthWithoutTruncation()
    {
        using var context = new ManifestContext();
        var path = context.InstallPath;
        for (var index = 0; index <= PhysicalStoreDeletionManifest.MaximumDepth; index++)
        {
            path = Path.Combine(path, "d");
            Directory.CreateDirectory(path);
        }

        AssertRefused(() => context.Capture());

        Assert.True(Directory.Exists(path));
    }

    [SupportedPhysicalStoreFact]
    public void Capture_CancellationPreservesTheInstall()
    {
        using var context = new ManifestContext();
        context.WritePayload("payload.bin", "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => context.Reader.Capture(
            context.InstallDirectory, context.Install, cancellation.Token));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.InstallPath, "payload.bin")));
    }

    private static string PathOf(PhysicalStoreDeletionManifestEntry entry) => string.Join('/', entry.RelativeComponents);

    private static void AssertRefused(Action action, PackageStoreAdmissionReason reason = PackageStoreAdmissionReason.UnknownAuthority)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(reason, error.Reason);
    }

    private sealed class ManifestContext : IDisposable
    {
        internal PhysicalStorePublicationTestContext Native { get; } = PhysicalStoreDirectoryPublicationTests.CreateContext();
        internal PhysicalStoreDirectoryHandle PackageParent { get; private set; } = null!;
        internal PhysicalStoreDirectoryHandle InstallDirectory { get; private set; } = null!;
        internal PhysicalStoreDeletionManifestReader Reader { get; }
        internal PackageInstallIdentity Install { get; }
        internal string InstallPath { get; private set; }
        internal string MarkerPath => Path.Combine(InstallPath, PackageInstallStore.CompletionMarkerFileName);

        internal ManifestContext()
        {
            InstallPath = Path.Combine(Native.ParentPath, "feed", "package", "1.0.0");
            try
            {
                using var feed = Native.FileSystem.CreateDirectoryExclusiveAt(Native.Parent, "feed");
                PackageParent = Native.FileSystem.CreateDirectoryExclusiveAt(feed, "package");
                InstallDirectory = Native.FileSystem.CreateDirectoryExclusiveAt(PackageParent, "1.0.0");
                using (var marker = Native.FileSystem.CreateFileExclusiveAt(InstallDirectory, PackageInstallStore.CompletionMarkerFileName))
                    Native.FileSystem.WriteNewControlFile(marker, ReadOnlyMemory<byte>.Empty);
                var markerId = Native.FileSystem.InspectChildNoFollow(InstallDirectory, PackageInstallStore.CompletionMarkerFileName)!.Identity;
                Install = new PackageInstallIdentity(new PhysicalRootIdentity(Native.FileSystem.InspectHandle(Native.Parent).Identity),
                    "package", "1.0.0", "feed/package/1.0.0", Native.FileSystem.InspectHandle(InstallDirectory).Identity,
                    ProtectionDigest.PackageInstallCompletionIdentity(markerId));
                Reader = new PhysicalStoreDeletionManifestReader(Native.FileSystem);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal PackageInstallIdentity Identity(PhysicalFileIdentity? directory = null, string? completion = null, PhysicalRootIdentity? root = null)
            => new(root ?? Install.Root, Install.PackageId, Install.Version, Install.RootRelativeInstallPath,
                directory ?? Install.DirectoryIdentity, completion ?? Install.CompletionIdentity);

        internal PhysicalStoreDeletionManifest Capture() => Reader.Capture(InstallDirectory, Install, CancellationToken.None);

        internal void WritePayload(string relativePath, string contents)
        {
            var path = Path.Combine(InstallPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        internal void MoveToQuarantine()
        {
            var name = $".nuplane-quarantine-{Guid.NewGuid():N}";
            InstallDirectory.Dispose();
            ((IPhysicalStoreDirectoryPublicationFileSystem)Native.FileSystem).PublishDirectoryNoReplaceAt(
                PackageParent, "1.0.0", Install.DirectoryIdentity, name);
            InstallDirectory = Native.FileSystem.OpenDirectoryChildNoFollow(PackageParent, name);
            InstallPath = Path.Combine(Native.ParentPath, "feed", "package", name);
        }

        public void Dispose()
        {
            try
            {
                InstallDirectory?.Dispose();
                PackageParent?.Dispose();
            }
            finally
            {
                Native.Dispose();
            }
        }
    }
}
