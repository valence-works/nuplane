using System.Collections.Immutable;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Captures bounded exact native entry evidence without mutating an installation.</summary>
/// <remarks>
/// The caller retains its verified root ancestry and coordinated ownership throughout capture.
/// The leaf and supplied identity alone do not establish root containment or a logical path binding.
/// Returned evidence is detached data, not admission or deletion authority. This reader never
/// opens a lexical path, reads payload contents, moves an entry or removes anything. Capture is
/// not an atomic namespace snapshot against writers outside the coordination protocol.
/// </remarks>
internal sealed class PhysicalStoreDeletionManifestReader
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreDirectoryNameFileSystem _directories;
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;

    internal PhysicalStoreDeletionManifestReader(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("Exact entry capture requires native file names and directory profiles.");
        _directories = files as IPhysicalStoreDirectoryNameFileSystem
            ?? throw Unsupported("Exact entry capture requires native directory names.");
        _enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw Unsupported("Exact entry capture requires bounded native directory enumeration.");
    }

    /// <summary>Captures a completed install or its exact same-identity quarantine tree.</summary>
    internal PhysicalStoreDeletionManifest Capture(
        PhysicalStoreDirectoryHandle installDirectory,
        PackageInstallIdentity expectedInstall,
        CancellationToken cancellationToken)
        => CaptureCore(installDirectory, expectedInstall, boundLocation: null, requireCompletionMarker: true, cancellationToken);

    /// <summary>Captures a complete current namespace and proves which immutable baseline rows are absent.</summary>
    /// <remarks>Absence is derived only after bounded no-follow enumeration of every surviving directory.</remarks>
    internal PhysicalStoreDeletionPartialObservation CapturePartial(
        PhysicalStoreDirectoryHandle directory,
        PackageInstallIdentity expectedInstall,
        PhysicalStoreDirectoryHandle expectedPackageParent,
        PhysicalFileIdentity expectedPackageParentIdentity,
        PhysicalStoreNameSemantics expectedPackageParentSemantics,
        string expectedDirectoryBasename,
        PhysicalStoreNameSemantics expectedDirectorySemantics,
        PhysicalStoreDeletionManifest baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var observed = CaptureCore(directory, expectedInstall,
            new BoundPackageParent(expectedPackageParent, expectedPackageParentIdentity,
                expectedPackageParentSemantics, expectedDirectoryBasename, expectedDirectorySemantics),
            requireCompletionMarker: false, cancellationToken);
        baseline.RequireExactSubset(observed);
        var observedPaths = observed.Entries
            .Select(static entry => PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents))
            .ToHashSet(StringComparer.Ordinal);
        var absent = baseline.Entries
            .Where(entry => !observedPaths.Contains(PhysicalStoreDeletionManifest.GetPathKey(entry.RelativeComponents)))
            .ToImmutableArray();
        return new PhysicalStoreDeletionPartialObservation(observed, absent);
    }

    private PhysicalStoreDeletionManifest CaptureCore(
        PhysicalStoreDirectoryHandle installDirectory,
        PackageInstallIdentity expectedInstall,
        BoundPackageParent? boundLocation,
        bool requireCompletionMarker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installDirectory);
        ArgumentNullException.ThrowIfNull(expectedInstall);
        cancellationToken.ThrowIfCancellationRequested();
        var installInfo = RequireDirectory(_files.InspectHandle(installDirectory), expectedInstall.DirectoryIdentity);
        PhysicalStoreDeletionChecks.RequireSameVolume(expectedInstall.Root.HandleIdentity, installInfo.Identity);
        if (installInfo.Identity == expectedInstall.Root.HandleIdentity)
            throw Unknown("An install tree cannot be the package-store root itself.");
        using var parent = _files.OpenParentDirectory(installDirectory);
        var parentInfo = _files.InspectHandle(parent);
        RequireDirectory(parentInfo, parentInfo.Identity);
        PhysicalStoreDeletionChecks.RequireSameVolume(installInfo.Identity, parentInfo.Identity);
        if (parentInfo.Identity == installInfo.Identity || parentInfo.Identity == expectedInstall.Root.HandleIdentity)
            throw Unknown("The install, its package parent and the store root must be distinct native directories.");

        var parentSemantics = Profile(parent, expectedInstall.Root);
        var semantics = Profile(installDirectory, expectedInstall.Root);
        if (boundLocation is not null)
            VerifyBoundLocation(parent, parentInfo.Identity, parentSemantics);
        var entries = new List<PhysicalStoreDeletionManifestEntry>();
        var identities = new HashSet<PhysicalFileIdentity>
            { installInfo.Identity, parentInfo.Identity, expectedInstall.Root.HandleIdentity };
        Walk(installDirectory, [], installInfo.Identity, semantics);

        var marker = entries.SingleOrDefault(entry => entry.RelativeComponents.Length == 1 &&
            entry.RelativeComponents[0] == PackageInstallStore.CompletionMarkerFileName);
        if (requireCompletionMarker &&
            (marker is null || marker.Kind != PhysicalStoreEntryKind.RegularFile ||
             ProtectionDigest.PackageInstallCompletionIdentity(marker.Identity) != expectedInstall.CompletionIdentity))
            throw Unknown("The captured completion marker does not match the original install identity.");
        RequireHeldDirectory(installDirectory, installInfo.Identity, semantics);
        RequireHeldDirectory(parent, parentInfo.Identity, parentSemantics);
        using var currentParent = _files.OpenParentDirectory(installDirectory);
        RequireHeldDirectory(currentParent, parentInfo.Identity, parentSemantics);
        if (boundLocation is not null)
            VerifyBoundLocation(currentParent, parentInfo.Identity, parentSemantics);
        return new PhysicalStoreDeletionManifest(installInfo.Identity, semantics, entries);

        void Walk(PhysicalStoreDirectoryHandle directory, string[] path,
            PhysicalFileIdentity identity, PhysicalStoreNameSemantics profile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireHeldDirectory(directory, identity, profile);
            var names = ReadNames(directory);
            if (names.Length > PhysicalStoreDeletionManifest.MaximumEntries - entries.Count ||
                (names.Length != 0 && path.Length >= PhysicalStoreDeletionManifest.MaximumDepth))
                throw Unknown("The complete install tree exceeds its fixed entry or depth bound.");

            var observedChildren = new List<(string Name, PhysicalFileIdentity Identity, PhysicalStoreEntryKind Kind)>(names.Length);
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { PhysicalStoreDeletionManifestEntry.ValidateComponent(name, profile); }
                catch (ArgumentException exception)
                {
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                        "A native install entry exceeds the supported component encoding or size bound.",
                        expectedInstall.Root, exception);
                }
                var childPath = path.Append(name).ToArray();
                var named = _files.InspectChildNoFollow(directory, name)
                    ?? throw Unknown("An entry disappeared after native enumeration.");
                PhysicalStoreDeletionChecks.RequireSameVolume(installInfo.Identity, named.Identity);
                if (!identities.Add(named.Identity) || named.ReparseTag is not (null or 0))
                    throw Unknown("The tree contains a repeated native identity or reparse entry.");

                if (named.Kind == PhysicalStoreEntryKind.Directory)
                {
                    using var child = _files.OpenDirectoryChildNoFollow(directory, name);
                    RequireDirectory(_files.InspectHandle(child), named.Identity);
                    RequireCanonical(_directories.ObserveCanonicalDirectoryNameNoFollow(directory, name, named.Identity),
                        name, identity, profile, named.Identity);
                    var childProfile = Profile(child, expectedInstall.Root);
                    entries.Add(new PhysicalStoreDeletionManifestEntry(childPath, named.Kind, named.Identity,
                        identity, profile, childProfile));
                    Walk(child, childPath, named.Identity, childProfile);
                    RequireDirectory(_files.InspectChildNoFollow(directory, name), named.Identity);
                    RequireHeldDirectory(child, named.Identity, childProfile);
                }
                else if (named.Kind == PhysicalStoreEntryKind.RegularFile)
                {
                    RequireFile(named);
                    using var child = _files.OpenFileChildNoFollow(directory, name, FileAccess.Read);
                    if (_files.InspectHandle(child) != named)
                        throw Unknown("A native file changed between no-follow inspection and open.");
                    RequireCanonical(_names.ObserveCanonicalFileNameNoFollow(directory, name, named.Identity),
                        name, identity, profile, named.Identity);
                    if (_files.InspectHandle(child) != named || _files.InspectChildNoFollow(directory, name) != named)
                        throw Unknown("The exact native file changed during canonical name observation.");
                    if (path.Length == 0 && name == PackageInstallStore.CompletionMarkerFileName && named.Length != 0)
                        throw Unknown("The install completion marker must be empty.");
                    entries.Add(new PhysicalStoreDeletionManifestEntry(childPath, named.Kind, named.Identity,
                        identity, profile, directorySemantics: null));
                }
                else
                {
                    throw Unknown("Only no-follow directories and single-link regular files belong in a deletion baseline.");
                }

                if (entries.Count > PhysicalStoreDeletionManifest.MaximumEntries)
                    throw Unknown("The complete install tree exceeds its fixed total-entry bound.");
                observedChildren.Add((name, named.Identity, named.Kind));
                RequireHeldDirectory(directory, identity, profile);
            }

            if (!names.SequenceEqual(ReadNames(directory), StringComparer.Ordinal))
                throw Unknown("The directory's native child names changed during tree capture.");
            foreach (var child in observedChildren)
            {
                var current = _files.InspectChildNoFollow(directory, child.Name);
                if (current is null || current.Identity != child.Identity || current.Kind != child.Kind ||
                    current.ReparseTag is not (null or 0))
                    throw Unknown("An observed child changed before tree capture completed.");
                if (current.Kind == PhysicalStoreEntryKind.RegularFile)
                    RequireFile(current);
            }
            RequireHeldDirectory(directory, identity, profile);
        }

        void VerifyBoundLocation(
            PhysicalStoreDirectoryHandle capturedParent,
            PhysicalFileIdentity capturedParentIdentity,
            PhysicalStoreNameSemantics capturedParentSemantics)
        {
            var expected = boundLocation!;
            if (capturedParentIdentity != expected.ParentIdentity ||
                capturedParentSemantics != expected.ParentSemantics ||
                semantics != expected.InstallDirectorySemantics)
            {
                throw Unknown("The manifest reader opened a different package parent than candidate binding observed.");
            }
            RequireHeldDirectory(expected.ParentHandle, expected.ParentIdentity, expected.ParentSemantics);

            var named = RequireDirectory(
                _files.InspectChildNoFollow(capturedParent, expected.OriginalBasename),
                expectedInstall.DirectoryIdentity);
            using var reopened = _files.OpenDirectoryChildNoFollow(capturedParent, expected.OriginalBasename);
            RequireDirectory(_files.InspectHandle(reopened), expectedInstall.DirectoryIdentity);
            if (Profile(reopened, expectedInstall.Root) != expected.InstallDirectorySemantics)
                throw Unknown("The bound install directory's name profile changed during manifest capture.");
            RequireCanonical(_directories.ObserveCanonicalDirectoryNameNoFollow(
                capturedParent, expected.OriginalBasename, expectedInstall.DirectoryIdentity),
                expected.OriginalBasename,
                expected.ParentIdentity,
                expected.ParentSemantics,
                expectedInstall.DirectoryIdentity);
            if (_files.InspectHandle(installDirectory).Identity != named.Identity ||
                _files.InspectChildNoFollow(capturedParent, expected.OriginalBasename) != named)
            {
                throw Unknown("The bound install basename changed during manifest capture.");
            }
            RequireHeldDirectory(expected.ParentHandle, expected.ParentIdentity, expected.ParentSemantics);
        }
    }

    /// <summary>Captures a baseline only when the traversal's opened parent and exact name match retained ancestry.</summary>
    /// <remarks>
    /// This overload joins the reader's fresh <c>..</c> handle to the same held package parent used by
    /// deletion candidate binding. It remains descriptive and grants no mutation authority.
    /// </remarks>
    internal PhysicalStoreDeletionManifest Capture(
        PhysicalStoreDirectoryHandle installDirectory,
        PackageInstallIdentity expectedInstall,
        PhysicalStoreDirectoryHandle expectedPackageParent,
        PhysicalFileIdentity expectedPackageParentIdentity,
        PhysicalStoreNameSemantics expectedPackageParentSemantics,
        string expectedInstallBasename,
        PhysicalStoreNameSemantics expectedInstallDirectorySemantics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installDirectory);
        ArgumentNullException.ThrowIfNull(expectedInstall);
        ArgumentNullException.ThrowIfNull(expectedPackageParent);
        ArgumentNullException.ThrowIfNull(expectedPackageParentIdentity);
        ArgumentNullException.ThrowIfNull(expectedPackageParentSemantics);
        ArgumentNullException.ThrowIfNull(expectedInstallDirectorySemantics);
        PhysicalStoreNames.ValidateSingleComponent(expectedInstallBasename);
        cancellationToken.ThrowIfCancellationRequested();

        return CaptureCore(installDirectory, expectedInstall,
            new BoundPackageParent(expectedPackageParent, expectedPackageParentIdentity,
                expectedPackageParentSemantics, expectedInstallBasename, expectedInstallDirectorySemantics),
            requireCompletionMarker: true,
            cancellationToken);
    }

    private sealed record BoundPackageParent(
        PhysicalStoreDirectoryHandle ParentHandle,
        PhysicalFileIdentity ParentIdentity,
        PhysicalStoreNameSemantics ParentSemantics,
        string OriginalBasename,
        PhysicalStoreNameSemantics InstallDirectorySemantics);

    private string[] ReadNames(PhysicalStoreDirectoryHandle directory)
    {
        var names = _enumeration.EnumerateChildNamesNoFollow(directory,
            PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory).ToArray();
        if (names.Length > PhysicalStoreDeletionManifest.MaximumChildrenPerDirectory ||
            names.Any(static name => name is null) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw Unknown("Native enumeration returned invalid, duplicate or excessive child names.");
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    private PhysicalStoreNameSemantics Profile(PhysicalStoreDirectoryHandle directory, PhysicalRootIdentity root)
        => PackageInstallStore.RequireSupportedNameProfile(_names.ObserveDirectoryNameSemantics(directory), root);

    private void RequireHeldDirectory(PhysicalStoreDirectoryHandle directory,
        PhysicalFileIdentity identity, PhysicalStoreNameSemantics semantics)
    {
        RequireDirectory(_files.InspectHandle(directory), identity);
        if (_names.ObserveDirectoryNameSemantics(directory) != semantics)
            throw Unknown("A held install directory's native name profile changed during capture.");
    }

    private static PhysicalStoreEntryInfo RequireDirectory(PhysicalStoreEntryInfo? entry, PhysicalFileIdentity identity)
    {
        if (entry is null || entry.Kind != PhysicalStoreEntryKind.Directory || entry.Identity != identity ||
            entry.ReparseTag is not (null or 0))
            throw Unknown("A held or named install directory is absent, replaced, linked or of the wrong kind.");
        return entry;
    }

    private static void RequireFile(PhysicalStoreEntryInfo entry)
    {
        if (entry.Kind != PhysicalStoreEntryKind.RegularFile || entry.LinkCount != 1 || entry.ReparseTag is not (null or 0))
            throw Unknown("An install file must be a regular single-link entry without reparse evidence.");
    }

    private static void RequireCanonical(PhysicalStoreCanonicalName observed, string name,
        PhysicalFileIdentity parent, PhysicalStoreNameSemantics semantics, PhysicalFileIdentity identity)
    {
        if (observed.Basename != name || observed.ParentIdentity != parent ||
            observed.Semantics != semantics || observed.FileIdentity != identity)
            throw Unknown("An install entry does not have its exact native name, parent, profile and identity.");
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);
}

internal sealed record PhysicalStoreDeletionPartialObservation(
    PhysicalStoreDeletionManifest Manifest,
    ImmutableArray<PhysicalStoreDeletionManifestEntry> PositivelyAbsentEntries);
