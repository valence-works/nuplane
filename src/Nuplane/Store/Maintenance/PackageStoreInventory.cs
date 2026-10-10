using System.Text;
using NuGet.Packaging;
using NuGet.Versioning;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Builds a bounded, no-follow description of completed installs beneath an admitted root.</summary>
internal sealed class PackageStoreInventory : IPackageStoreInventory
{
    private const int MaximumDirectoryEntries = 1024;
    private const int MaximumTotalEntries = 16384;
    private const int MaximumInstallPathDepth = 3;
    private const string LegacyStagingName = ".tmp";
    private const string NativeStagePrefix = ".nuplane-stage-";
    private const string PreparedPrefix = ".nuplane-prepared-";
    private const int MaximumComponentBytes = 255;

    private static readonly string[] ReservedRootNames = [RootMembershipRegistry.ControlDirectoryName, LegacyStagingName];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly PackageInstallIdentityReader _identityReader;

    internal PackageStoreInventory(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw Unsupported("The filesystem does not support bounded native directory enumeration.");
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("The filesystem cannot verify native directory name profiles.");
        _identityReader = new PackageInstallIdentityReader(files);
    }

    /// <inheritdoc />
    public Task<PackageStoreInventorySnapshot> ReadAsync(
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return PackageStoreOperationAccess.WithValidatedRootAsync(
            borrow,
            (files, root, token) =>
            {
                if (!ReferenceEquals(files, _files))
                {
                    throw new PackageStoreAdmissionException(
                        PackageStoreAdmissionReason.UnsupportedParticipant,
                        "The borrow's native filesystem provider differs from this inventory instance.",
                        borrow.Root);
                }

                return Task.FromResult(ReadCore(root, borrow.Root, borrow.Epoch, token));
            },
            cancellationToken);
    }

    private PackageStoreInventorySnapshot ReadCore(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long epoch,
        CancellationToken cancellationToken)
    {
        var walk = new InventoryWalk { Root = root };
        PhysicalStoreNameSemantics rootSemantics;
        try
        {
            var rootInfo = _files.InspectHandle(root);
            if (rootInfo.Kind != PhysicalStoreEntryKind.Directory || rootInfo.Identity != expectedRoot.HandleIdentity)
            {
                walk.AddIssue(string.Empty, "The held root no longer matches the admitted physical root identity.");
                return walk.ToSnapshot(expectedRoot, epoch);
            }

            RequireSameVolume(rootInfo.Identity, expectedRoot.HandleIdentity, expectedRoot);
            rootSemantics = ObserveSupportedSemantics(root, expectedRoot);
            ScanRoot(walk, root, rootInfo.Identity, rootSemantics, expectedRoot, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var rootAfter = _files.InspectHandle(root);
            var semanticsAfter = ObserveSupportedSemantics(root, expectedRoot);
            if (rootAfter.Kind != PhysicalStoreEntryKind.Directory || rootAfter.Identity != rootInfo.Identity ||
                semanticsAfter != rootSemantics)
            {
                walk.AddIssue(string.Empty, "The held root identity, kind, or native name profile changed during inventory.");
            }
        }
        catch (Exception exception) when (IsInventoryObservationFailure(exception))
        {
            walk.AddIssue(string.Empty, DescribeFailure(exception));
        }

        return walk.ToSnapshot(expectedRoot, epoch);
    }

    private void ScanRoot(
        InventoryWalk walk,
        PhysicalStoreDirectoryHandle root,
        PhysicalFileIdentity rootIdentity,
        PhysicalStoreNameSemantics rootSemantics,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        var children = ObserveChildren(walk, root, string.Empty, cancellationToken);
        if (children is null || !CheckForDirectoryAliases(walk, children))
            return;

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!walk.IsComplete)
                return;

            if (string.Equals(child.Name, RootMembershipRegistry.ControlDirectoryName, StringComparison.Ordinal))
            {
                ClassifyReservedRootDirectory(walk, root, rootIdentity, rootSemantics, child, expectedRoot,
                    PackageStoreInventoryEntryKind.ControlDirectory, cancellationToken);
                continue;
            }

            if (string.Equals(child.Name, LegacyStagingName, StringComparison.Ordinal))
            {
                ClassifyReservedRootDirectory(walk, root, rootIdentity, rootSemantics, child, expectedRoot,
                    PackageStoreInventoryEntryKind.LegacyStagingDirectory, cancellationToken);
                continue;
            }

            var aliasesReservedName = IsAliasOfReservedRootName(walk, root, rootSemantics, child);
            if (!walk.IsComplete)
                return;
            if (aliasesReservedName)
            {
                walk.AddEntry(new PackageStoreInventoryEntry(child.Path, PackageStoreInventoryEntryKind.Unknown,
                    "The stored root name aliases a reserved control or legacy-staging name."));
                continue;
            }

            if (child.Info.Kind != PhysicalStoreEntryKind.Directory)
            {
                walk.AddEntry(Unknown(child, "A store-root entry is not a feed directory."));
                continue;
            }

            if (!IsSameVolume(child.Info.Identity, expectedRoot.HandleIdentity))
            {
                walk.AddEntry(Unknown(child, "The feed directory is on a different native provider or volume."));
                continue;
            }

            using var feed = TryOpenDirectory(walk, root, rootIdentity, rootSemantics, child, expectedRoot);
            if (feed is null)
                return;

            walk.AddEntry(new PackageStoreInventoryEntry(child.Path, PackageStoreInventoryEntryKind.FeedDirectory));
            ScanFeed(walk, feed, expectedRoot, cancellationToken);
            if (!walk.IsComplete)
                return;

            feed.Revalidate();
        }
    }

    private void ClassifyReservedRootDirectory(
        InventoryWalk walk,
        PhysicalStoreDirectoryHandle root,
        PhysicalFileIdentity rootIdentity,
        PhysicalStoreNameSemantics rootSemantics,
        ObservedChild child,
        PhysicalRootIdentity expectedRoot,
        PackageStoreInventoryEntryKind kind,
        CancellationToken cancellationToken)
    {
        if (child.Info.Kind != PhysicalStoreEntryKind.Directory)
        {
            walk.AddEntry(Unknown(child, "A reserved root name is present with a non-directory filesystem kind."));
            return;
        }

        if (!IsSameVolume(child.Info.Identity, expectedRoot.HandleIdentity))
        {
            walk.AddEntry(Unknown(child, "A reserved root directory is on a different native provider or volume."));
            return;
        }

        using var reserved = TryOpenDirectory(walk, root, rootIdentity, rootSemantics, child, expectedRoot);
        if (reserved is null)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        reserved.Revalidate();
        walk.AddEntry(new PackageStoreInventoryEntry(child.Path, kind));
    }

    private bool IsAliasOfReservedRootName(
        InventoryWalk walk,
        PhysicalStoreDirectoryHandle root,
        PhysicalStoreNameSemantics rootSemantics,
        ObservedChild child)
    {
        if (rootSemantics.CaseSensitive && !rootSemantics.NormalizationInsensitive)
            return false;

        foreach (var reservedName in ReservedRootNames)
        {
            if (string.Equals(child.Name, reservedName, StringComparison.Ordinal))
                continue;

            try
            {
                var reserved = _files.InspectChildNoFollow(root, reservedName);
                if (reserved?.Kind == PhysicalStoreEntryKind.Directory && reserved.Identity == child.Info.Identity)
                    return true;
            }
            catch (Exception exception) when (IsInventoryObservationFailure(exception))
            {
                walk.AddIssue(child.Path, $"Could not establish whether the root name aliases '{reservedName}': {DescribeFailure(exception)}");
                return false;
            }
        }

        return false;
    }

    private void ScanFeed(
        InventoryWalk walk,
        OwnedDirectory feed,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        var children = ObserveChildren(walk, feed.Handle, feed.Path, cancellationToken);
        if (children is null || !CheckForDirectoryAliases(walk, children))
            return;

        var packageGroups = children
            .Where(static child => child.Info.Kind == PhysicalStoreEntryKind.Directory && PackageIdValidator.IsValidPackageId(child.Name))
            .GroupBy(static child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!walk.IsComplete)
                return;

            if (child.Info.Kind != PhysicalStoreEntryKind.Directory)
            {
                walk.AddEntry(Unknown(child, "A feed entry is not a package directory."));
                continue;
            }

            if (!PackageIdValidator.IsValidPackageId(child.Name))
            {
                walk.AddEntry(Unknown(child, "The directory name is not a valid NuGet package identifier."));
                continue;
            }

            if (packageGroups[child.Name].Length > 1)
            {
                walk.AddEntry(Unknown(child, "Multiple native directories normalize to the same case-insensitive NuGet package identifier."));
                continue;
            }

            if (!IsSameVolume(child.Info.Identity, expectedRoot.HandleIdentity))
            {
                walk.AddEntry(Unknown(child, "The package directory is on a different native provider or volume."));
                continue;
            }

            using var package = TryOpenDirectory(walk, feed.Handle, feed.Identity, feed.Semantics, child, expectedRoot);
            if (package is null)
                return;

            walk.AddEntry(new PackageStoreInventoryEntry(child.Path, PackageStoreInventoryEntryKind.PackageDirectory));
            ScanPackage(walk, package, child.Name, expectedRoot, cancellationToken);
            if (!walk.IsComplete)
                return;

            package.Revalidate();
        }

        feed.Revalidate();
    }

    private void ScanPackage(
        InventoryWalk walk,
        OwnedDirectory package,
        string packageId,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        var children = ObserveChildren(walk, package.Handle, package.Path, cancellationToken);
        if (children is null || !CheckForDirectoryAliases(walk, children))
            return;

        // Inventory identifies exact install paths, not retention release groups. Keep build
        // metadata in the normalized key; only collapse spellings that normalize to one path.
        // On a case-insensitive native directory, compare the ASCII version component using the
        // corresponding path semantics. NuGet version syntax is ASCII, so ordinal ignore-case is
        // sufficient for aliases exposed by supported store profiles.
        var versionNameComparer = package.Semantics.CaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var versionGroups = new Dictionary<string, List<ObservedChild>>(versionNameComparer);
        foreach (var child in children)
        {
            if (IsCanonicalResidueName(child.Name, NativeStagePrefix, out _) ||
                IsCanonicalResidueName(child.Name, PreparedPrefix, out _))
            {
                if (child.Info.Kind != PhysicalStoreEntryKind.Directory)
                    walk.AddEntry(Unknown(child, "A native staging-style name does not identify a directory."));
                else if (!IsSameVolume(child.Info.Identity, expectedRoot.HandleIdentity))
                    walk.AddEntry(Unknown(child, "A native staging directory is on a different native provider or volume."));
                else
                {
                    var residueKind = child.Name.StartsWith(NativeStagePrefix, StringComparison.Ordinal)
                        ? PackageStoreInventoryEntryKind.NativeStagingResidue
                        : PackageStoreInventoryEntryKind.PreparedResidue;
                    using var residue = TryOpenDirectory(walk, package.Handle, package.Identity, package.Semantics, child, expectedRoot);
                    if (residue is null)
                        return;
                    residue.Revalidate();
                    walk.AddEntry(new PackageStoreInventoryEntry(child.Path, residueKind));
                }

                continue;
            }

            if (child.Name.StartsWith(NativeStagePrefix, StringComparison.Ordinal) ||
                child.Name.StartsWith(PreparedPrefix, StringComparison.Ordinal))
            {
                walk.AddEntry(Unknown(child, "A staging-style name is not a canonical native installer residue name."));
                continue;
            }

            if (child.Info.Kind != PhysicalStoreEntryKind.Directory)
            {
                walk.AddEntry(Unknown(child, "A package child is not a version directory."));
                continue;
            }

            if (!IsSameVolume(child.Info.Identity, expectedRoot.HandleIdentity))
            {
                walk.AddEntry(Unknown(child, "The version directory is on a different native provider or volume."));
                continue;
            }

            if (!NuGetVersion.TryParse(child.Name, out var version))
            {
                walk.AddEntry(new PackageStoreInventoryEntry(child.Path,
                    PackageStoreInventoryEntryKind.IncompleteInstall,
                    "The directory name is not a valid NuGet version."));
                continue;
            }

            var normalizedVersion = NormalizeFullVersion(version);
            if (!versionGroups.TryGetValue(normalizedVersion, out var versions))
            {
                versions = [];
                versionGroups.Add(normalizedVersion, versions);
            }

            versions.Add(child);
        }

        foreach (var versionGroup in versionGroups.Values.OrderBy(static group => group[0].Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!walk.IsComplete)
                return;

            if (versionGroup.Count > 1)
            {
                foreach (var duplicateChild in versionGroup.OrderBy(static child => child.Path, StringComparer.Ordinal))
                {
                    walk.AddEntry(Unknown(duplicateChild, "Multiple native version-directory names normalize to the same NuGet version."));
                }

                continue;
            }

            var versionChild = versionGroup[0];
            var version = NuGetVersion.Parse(versionChild.Name);
            ObserveInstallCandidate(walk, package, versionChild, packageId, version, expectedRoot, cancellationToken);
        }

        package.Revalidate();
    }

    private void ObserveInstallCandidate(
        InventoryWalk walk,
        OwnedDirectory package,
        ObservedChild versionChild,
        string packageId,
        NuGetVersion version,
        PhysicalRootIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        if (versionChild.Path.Count(static character => character == '/') + 1 != MaximumInstallPathDepth)
        {
            walk.AddIssue(versionChild.Path, "A candidate path does not have the fixed feed/package/version depth.");
            walk.AddEntry(Unknown(versionChild, "The install path depth is outside the supported native store layout."));
            return;
        }

        using var versionDirectory = TryOpenDirectory(
            walk, package.Handle, package.Identity, package.Semantics, versionChild, expectedRoot);
        if (versionDirectory is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        PhysicalStoreEntryInfo? marker;
        var normalizedFullVersion = NormalizeFullVersion(version);
        try
        {
            marker = _files.InspectChildNoFollow(versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName);
            if (marker is null)
            {
                versionDirectory.Revalidate();
                if (_files.InspectChildNoFollow(versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName) is not null)
                    throw Uncertain("The completion marker appeared while inventory was classifying an incomplete install.", expectedRoot);

                walk.AddEntry(new PackageStoreInventoryEntry(versionChild.Path,
                    PackageStoreInventoryEntryKind.IncompleteInstall,
                    "The completion marker is missing."));
                return;
            }

            if (marker.Kind != PhysicalStoreEntryKind.RegularFile || marker.LinkCount != 1 || marker.Length != 0)
            {
                versionDirectory.Revalidate();
                var markerAfter = _files.InspectChildNoFollow(versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName);
                if (markerAfter != marker)
                    throw Uncertain("The completion marker changed while inventory was classifying an incomplete install.", expectedRoot);

                walk.AddEntry(new PackageStoreInventoryEntry(versionChild.Path,
                    PackageStoreInventoryEntryKind.IncompleteInstall,
                    "The completion marker is not one empty single-link regular file."));
                return;
            }

            var canonicalMarker = _names.ObserveCanonicalFileNameNoFollow(
                versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName, marker.Identity);
            if (!string.Equals(canonicalMarker.Basename, PackageInstallStore.CompletionMarkerFileName, StringComparison.Ordinal) ||
                canonicalMarker.ParentIdentity != versionDirectory.Identity ||
                canonicalMarker.FileIdentity != marker.Identity || canonicalMarker.Semantics != versionDirectory.Semantics)
            {
                versionDirectory.Revalidate();
                if (_files.InspectChildNoFollow(versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName) != marker)
                    throw Uncertain("The aliased completion marker changed during native name verification.", expectedRoot);
                walk.AddEntry(new PackageStoreInventoryEntry(versionChild.Path,
                    PackageStoreInventoryEntryKind.IncompleteInstall,
                    "The completion marker is stored under an unsupported native name alias."));
                return;
            }

            using var observation = _identityReader.Observe(
                versionDirectory.Root,
                expectedRoot,
                versionChild.Path,
                packageId,
                normalizedFullVersion);
            observation.Revalidate();
            versionDirectory.Revalidate();
            var markerAfterObservation = _files.InspectChildNoFollow(
                versionDirectory.Handle, PackageInstallStore.CompletionMarkerFileName);
            if (markerAfterObservation != marker)
                throw Uncertain("The completion marker changed during install identity observation.", expectedRoot);

            var identity = observation.InstallIdentity;
            if (identity.Root != expectedRoot ||
                !string.Equals(identity.RootRelativeInstallPath, versionChild.Path, StringComparison.Ordinal) ||
                !string.Equals(identity.PackageId, packageId, StringComparison.Ordinal) ||
                !string.Equals(identity.Version, normalizedFullVersion, StringComparison.Ordinal) ||
                !VersionComparer.VersionRelease.Equals(NuGetVersion.Parse(identity.Version), version))
            {
                throw Uncertain("The native install identity does not match the enumerated package path.", expectedRoot);
            }

            walk.AddEntry(new PackageStoreInventoryEntry(
                versionChild.Path,
                PackageStoreInventoryEntryKind.CompletedInstallCandidate,
                installIdentity: identity));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsInventoryObservationFailure(exception))
        {
            var reason = DescribeFailure(exception);
            walk.AddEntry(new PackageStoreInventoryEntry(versionChild.Path,
                PackageStoreInventoryEntryKind.Unknown,
                $"Native install identity could not be established: {reason}"));
            walk.AddIssue(versionChild.Path, reason);
        }
    }

    private OwnedDirectory? TryOpenDirectory(
        InventoryWalk walk,
        PhysicalStoreDirectoryHandle parent,
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics parentSemantics,
        ObservedChild child,
        PhysicalRootIdentity expectedRoot)
    {
        PhysicalStoreDirectoryHandle? handle = null;
        try
        {
            ValidateInstallerComponent(child.Name, parentSemantics, expectedRoot);
            var parentBefore = _files.InspectHandle(parent);
            if (parentBefore.Kind != PhysicalStoreEntryKind.Directory || parentBefore.Identity != parentIdentity ||
                ObserveSupportedSemantics(parent, expectedRoot) != parentSemantics)
            {
                throw Uncertain("The held directory parent changed before opening an enumerated child.", expectedRoot);
            }

            var namedBefore = _files.InspectChildNoFollow(parent, child.Name);
            if (namedBefore is null || namedBefore.Kind != PhysicalStoreEntryKind.Directory || namedBefore.Identity != child.Info.Identity)
                throw Uncertain("An enumerated directory entry disappeared or changed before it was opened.", expectedRoot);
            RequireSameVolume(namedBefore.Identity, expectedRoot.HandleIdentity, expectedRoot);

            handle = _files.OpenDirectoryChildNoFollow(parent, child.Name);
            var opened = _files.InspectHandle(handle);
            if (opened.Kind != PhysicalStoreEntryKind.Directory || opened.Identity != namedBefore.Identity)
                throw Uncertain("An enumerated directory entry changed identity while it was opened.", expectedRoot);
            RequireSameVolume(opened.Identity, expectedRoot.HandleIdentity, expectedRoot);
            var semantics = ObserveSupportedSemantics(handle, expectedRoot);

            var namedAfter = _files.InspectChildNoFollow(parent, child.Name);
            var parentAfter = _files.InspectHandle(parent);
            var heldAfter = _files.InspectHandle(handle);
            if (namedAfter?.Kind != PhysicalStoreEntryKind.Directory || namedAfter.Identity != opened.Identity ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != parentIdentity ||
                heldAfter.Kind != PhysicalStoreEntryKind.Directory || heldAfter.Identity != opened.Identity ||
                ObserveSupportedSemantics(parent, expectedRoot) != parentSemantics ||
                ObserveSupportedSemantics(handle, expectedRoot) != semantics)
            {
                throw Uncertain("The enumerated directory edge changed during native open and profile verification.", expectedRoot);
            }

            var owned = new OwnedDirectory(_files, _names, walk.Root, expectedRoot,
                parent, parentIdentity, parentSemantics, child.Name, child.Path, handle, opened.Identity, semantics);
            handle = null;
            return owned;
        }
        catch (Exception exception) when (IsInventoryObservationFailure(exception))
        {
            handle?.Dispose();
            walk.AddIssue(child.Path, DescribeFailure(exception));
            return null;
        }
    }

    private ObservedChild[]? ObserveChildren(
        InventoryWalk walk,
        PhysicalStoreDirectoryHandle parent,
        string parentPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = MaximumTotalEntries - walk.ObservedNameCount;
        if (remaining <= 0)
        {
            walk.AddIssue(parentPath, $"The inventory exceeded its fixed total-entry bound of {MaximumTotalEntries}.");
            return null;
        }

        IReadOnlyList<string> names;
        try
        {
            names = _enumeration.EnumerateChildNamesNoFollow(parent, Math.Min(MaximumDirectoryEntries, remaining));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsInventoryObservationFailure(exception))
        {
            walk.AddIssue(parentPath, DescribeFailure(exception));
            return null;
        }

        if (names.Count > Math.Min(MaximumDirectoryEntries, remaining))
        {
            walk.AddIssue(parentPath, "The native directory enumerator returned more names than the fixed inventory bound.");
            return null;
        }

        walk.ObservedNameCount += names.Count;
        var observed = new List<ObservedChild>(names.Count);
        foreach (var name in names.OrderBy(static value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Join(parentPath, name);
            try
            {
                var parentBefore = _files.InspectHandle(parent);
                if (parentBefore.Kind != PhysicalStoreEntryKind.Directory)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                        "A held directory changed kind while inventory inspected its children.");

                var first = _files.InspectChildNoFollow(parent, name);
                if (first is null)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                        "A name disappeared after native directory enumeration.");
                var second = _files.InspectChildNoFollow(parent, name);
                var parentAfter = _files.InspectHandle(parent);
                if (second is null || second != first || parentAfter.Kind != PhysicalStoreEntryKind.Directory ||
                    parentAfter.Identity != parentBefore.Identity)
                {
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                        "An entry or its held parent changed during no-follow metadata observation.");
                }

                observed.Add(new ObservedChild(name, path, first));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsInventoryObservationFailure(exception))
            {
                walk.AddEntry(new PackageStoreInventoryEntry(path, PackageStoreInventoryEntryKind.Unknown,
                    $"Native entry metadata is uncertain: {DescribeFailure(exception)}"));
                walk.AddIssue(path, DescribeFailure(exception));
                return null;
            }
        }

        return observed.ToArray();
    }

    private static bool CheckForDirectoryAliases(InventoryWalk walk, IReadOnlyList<ObservedChild> children)
    {
        var duplicateDirectories = children
            .Where(static child => child.Info.Kind == PhysicalStoreEntryKind.Directory)
            .GroupBy(static child => child.Info.Identity)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToArray();
        if (duplicateDirectories.Length == 0)
            return true;

        foreach (var child in duplicateDirectories.OrderBy(static child => child.Path, StringComparer.Ordinal))
        {
            walk.AddEntry(Unknown(child, "Multiple native names resolve to the same directory identity."));
            walk.AddIssue(child.Path, "A directory identity is reachable through multiple native names.");
        }

        return false;
    }

    private bool IsCanonicalResidueName(string name, string prefix, out Guid operationId)
    {
        operationId = default;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = name[prefix.Length..];
        return Guid.TryParseExact(suffix, "N", out operationId) && operationId != Guid.Empty &&
               string.Equals(operationId.ToString("N"), suffix, StringComparison.Ordinal);
    }

    private static string NormalizeFullVersion(NuGetVersion version)
    {
        // NuGet's normalized version intentionally omits build metadata. Preserve metadata for an
        // exact install identity and path key while normalizing the version/release portion.
        var normalized = version.ToNormalizedString();
        return version.HasMetadata ? $"{normalized}+{version.Metadata}" : normalized;
    }

    private PhysicalStoreNameSemantics ObserveSupportedSemantics(
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity expectedRoot)
        => PackageInstallStore.RequireSupportedNameProfile(
            _names.ObserveDirectoryNameSemantics(directory), expectedRoot);

    private static void ValidateInstallerComponent(
        string component,
        PhysicalStoreNameSemantics semantics,
        PhysicalRootIdentity expectedRoot)
    {
        int encodedLength;
        try
        {
            encodedLength = semantics.Encoding == PhysicalStoreNameEncoding.Utf8
                ? StrictUtf8.GetByteCount(component)
                : StrictUtf16.GetByteCount(component);
        }
        catch (EncoderFallbackException exception)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "A store path component is not valid under the observed native name encoding.", expectedRoot, exception);
        }

        if (component.Length > MaximumComponentBytes || encodedLength > MaximumComponentBytes ||
            component.EndsWith(' ') || component.EndsWith('.') || component.Any(char.IsControl) ||
            component.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || IsReservedWindowsDeviceName(component))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "A store path component is not a canonical package-installer name.", expectedRoot);
        }

        if ((!semantics.CaseSensitive || semantics.NormalizationInsensitive) &&
            component.Any(static character => character > 0x7F))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "A non-ASCII store path component cannot be compared safely under the native name profile.", expectedRoot);
        }
    }

    private static bool IsReservedWindowsDeviceName(string component)
    {
        var stem = component.Split('.', 2)[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static bool IsSameVolume(PhysicalFileIdentity identity, PhysicalFileIdentity rootIdentity)
        => string.Equals(identity.Provider, rootIdentity.Provider, StringComparison.Ordinal) &&
           string.Equals(identity.VolumeOrDeviceId, rootIdentity.VolumeOrDeviceId, StringComparison.Ordinal);

    private static void RequireSameVolume(
        PhysicalFileIdentity identity,
        PhysicalFileIdentity rootIdentity,
        PhysicalRootIdentity expectedRoot)
    {
        if (!IsSameVolume(identity, rootIdentity))
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "A package-store component is on a different native provider or volume.", expectedRoot);
        }
    }

    private static PackageStoreInventoryEntry Unknown(ObservedChild child, string reason)
        => new(child.Path, PackageStoreInventoryEntryKind.Unknown, reason);

    private static string Join(string parent, string name)
        => parent.Length == 0 ? name : parent + "/" + name;

    private static bool IsInventoryObservationFailure(Exception exception)
        => exception is PackageStoreAdmissionException or IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException;

    private static string DescribeFailure(Exception exception)
        => exception is PackageStoreAdmissionException admission
            ? $"{admission.Reason}: {admission.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";

    private static PackageStoreAdmissionException Uncertain(string message, PhysicalRootIdentity root)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private sealed record ObservedChild(string Name, string Path, PhysicalStoreEntryInfo Info);

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly PhysicalStoreDirectoryHandle _parent;
        private readonly PhysicalFileIdentity _parentIdentity;
        private readonly PhysicalStoreNameSemantics _parentSemantics;
        private bool _disposed;

        internal OwnedDirectory(
            IPhysicalStoreFileSystem files,
            IPhysicalStoreNameFileSystem names,
            PhysicalStoreDirectoryHandle root,
            PhysicalRootIdentity expectedRoot,
            PhysicalStoreDirectoryHandle parent,
            PhysicalFileIdentity parentIdentity,
            PhysicalStoreNameSemantics parentSemantics,
            string name,
            string path,
            PhysicalStoreDirectoryHandle handle,
            PhysicalFileIdentity identity,
            PhysicalStoreNameSemantics semantics)
        {
            _files = files;
            _names = names;
            Root = root;
            ExpectedRoot = expectedRoot;
            _parent = parent;
            _parentIdentity = parentIdentity;
            _parentSemantics = parentSemantics;
            Name = name;
            Path = path;
            Handle = handle;
            Identity = identity;
            Semantics = semantics;
        }

        internal PhysicalStoreDirectoryHandle Root { get; }
        internal PhysicalRootIdentity ExpectedRoot { get; }
        internal PhysicalStoreDirectoryHandle Handle { get; }
        internal PhysicalFileIdentity Identity { get; }
        internal PhysicalStoreNameSemantics Semantics { get; }
        internal string Name { get; }
        internal string Path { get; }

        internal void Revalidate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var parentBefore = _files.InspectHandle(_parent);
            var heldBefore = _files.InspectHandle(Handle);
            if (parentBefore.Kind != PhysicalStoreEntryKind.Directory || parentBefore.Identity != _parentIdentity ||
                heldBefore.Kind != PhysicalStoreEntryKind.Directory || heldBefore.Identity != Identity ||
                _names.ObserveDirectoryNameSemantics(_parent) != _parentSemantics ||
                _names.ObserveDirectoryNameSemantics(Handle) != Semantics)
            {
                throw Uncertain("A held package-store directory or its name profile changed during inventory.", ExpectedRoot);
            }

            var named = _files.InspectChildNoFollow(_parent, Name);
            if (named?.Kind != PhysicalStoreEntryKind.Directory || named.Identity != Identity)
                throw Uncertain("A held package-store directory no longer matches its enumerated parent edge.", ExpectedRoot);

            using var reopened = _files.OpenDirectoryChildNoFollow(_parent, Name);
            var reopenedInfo = _files.InspectHandle(reopened);
            var parentAfter = _files.InspectHandle(_parent);
            var heldAfter = _files.InspectHandle(Handle);
            if (reopenedInfo.Kind != PhysicalStoreEntryKind.Directory || reopenedInfo.Identity != Identity ||
                parentAfter.Kind != PhysicalStoreEntryKind.Directory || parentAfter.Identity != _parentIdentity ||
                heldAfter.Kind != PhysicalStoreEntryKind.Directory || heldAfter.Identity != Identity ||
                _names.ObserveDirectoryNameSemantics(_parent) != _parentSemantics ||
                _names.ObserveDirectoryNameSemantics(Handle) != Semantics)
            {
                throw Uncertain("A package-store directory edge changed during final native revalidation.", ExpectedRoot);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Handle.Dispose();
        }
    }

    private sealed class InventoryWalk
    {
        private readonly List<PackageStoreInventoryEntry> _entries = [];
        private readonly List<PackageStoreInventoryIssue> _issues = [];

        internal PhysicalStoreDirectoryHandle Root { get; set; } = null!;
        internal int ObservedNameCount { get; set; }
        internal bool IsComplete => _issues.Count == 0;

        internal void AddEntry(PackageStoreInventoryEntry entry) => _entries.Add(entry);
        internal void AddIssue(string path, string reason) => _issues.Add(new PackageStoreInventoryIssue(path, reason));

        internal PackageStoreInventorySnapshot ToSnapshot(PhysicalRootIdentity root, long epoch)
            => new(root, epoch, IsComplete, _entries, _issues);
    }
}
