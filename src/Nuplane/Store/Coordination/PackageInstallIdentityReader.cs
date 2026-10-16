using NuGet.Versioning;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Observes one known completed install through native no-follow handles.</summary>
/// <remarks>
/// The returned value is descriptive candidate data, not package-store admission or proof that the
/// caller verified package contents. This reader opens only path components and the completion
/// marker; it does not enumerate package directories or read payload/hash bytes.
/// </remarks>
internal sealed class PackageInstallIdentityReader
{
    private const string StagingDirectoryName = ".tmp";
    private static readonly string[] ReservedRootDirectoryNames = [RootMembershipRegistry.ControlDirectoryName, StagingDirectoryName];

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;

    internal PackageInstallIdentityReader(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unsupported("The filesystem cannot observe native directory name profiles.");
    }

    /// <summary>Observes and retains the exact native identity evidence for one completed installation.</summary>
    /// <param name="verifiedRoot">The already-held store root; ownership remains with the caller.</param>
    /// <param name="expectedRoot">The expected native identity of <paramref name="verifiedRoot"/>.</param>
    /// <param name="rootRelativeInstallPath">The exact normalized feed/package/version path hint.</param>
    /// <param name="packageId">The NuGet package identifier associated with the install.</param>
    /// <param name="version">The selected NuGet version associated with the install.</param>
    /// <param name="alreadyVerifiedArchiveHash">Optional descriptive archive hash verified by the caller.</param>
    /// <returns>A disposable observation that retains all path directories and the completion marker.</returns>
    /// <exception cref="PackageStoreAdmissionException">Native metadata, spelling, volume, or completion evidence is ambiguous or inconsistent.</exception>
    /// <exception cref="ArgumentException">The relative path or package identity is malformed.</exception>
    internal PackageInstallIdentityObservation Observe(
        PhysicalStoreDirectoryHandle verifiedRoot,
        PhysicalRootIdentity expectedRoot,
        string rootRelativeInstallPath,
        string packageId,
        string version,
        string? alreadyVerifiedArchiveHash = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedRoot);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var components = ValidateRelativeInstallPath(rootRelativeInstallPath, packageId, version);

        var rootInfo = _files.InspectHandle(verifiedRoot);
        if (rootInfo.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown("The supplied package-install root handle is not a directory.", expectedRoot);
        if (rootInfo.Identity != expectedRoot.HandleIdentity)
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.RootMismatch,
                "The supplied package-install root does not match its expected physical identity.",
                expectedRoot);
        var rootSemantics = ObserveSemantics(verifiedRoot, expectedRoot);
        RequireSameVolume(rootInfo.Identity, expectedRoot.HandleIdentity, expectedRoot);

        var ownedHandles = new List<PhysicalStoreHandle>(components.Length + 1);
        var edges = new List<DirectoryEdgeObservation>(components.Length);
        var current = verifiedRoot;
        var currentIdentity = rootInfo.Identity;
        var currentSemantics = rootSemantics;

        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var parentBefore = _files.InspectHandle(current);
                RequireDirectory(parentBefore, currentIdentity, expectedRoot, "A held install-path parent changed.");
                RequireSameSemantics(current, currentSemantics, expectedRoot);

                var namedBefore = _files.InspectChildNoFollow(current, component)
                    ?? throw Unknown($"Install path component '{component}' is missing.", expectedRoot);
                RequireDirectoryEntry(namedBefore, expectedRoot, $"Install path component '{component}' is not a directory.");

                var child = _files.OpenDirectoryChildNoFollow(current, component);
                ownedHandles.Add(child);
                var openedChild = _files.InspectHandle(child);
                RequireDirectoryEntry(
                    openedChild,
                    expectedRoot,
                    $"Install path component '{component}' did not open as a same-volume directory.");
                if (openedChild.Identity != namedBefore.Identity)
                    throw Unknown($"Install path component '{component}' changed between inspection and open.", expectedRoot);

                if (index == 0 && IsReservedRootDirectory(_files, verifiedRoot, component, openedChild.Identity, currentSemantics))
                    throw Unknown("A package install cannot be located in the reserved control or staging directory.", expectedRoot);

                var childSemantics = ObserveSemantics(child, expectedRoot);
                var namedAfter = _files.InspectChildNoFollow(current, component);
                var parentAfter = _files.InspectHandle(current);
                var parentSemanticsAfter = ObserveSemantics(current, expectedRoot);
                var heldChildAfter = _files.InspectHandle(child);
                RequireDirectory(parentAfter, currentIdentity, expectedRoot, "An install-path parent changed during observation.");
                RequireDirectoryEntry(namedAfter, expectedRoot, $"Install path component '{component}' changed during observation.");
                RequireDirectory(
                    heldChildAfter,
                    openedChild.Identity,
                    expectedRoot,
                    "A held install-path directory changed during observation.");
                if (namedAfter!.Identity != openedChild.Identity || heldChildAfter.Identity != openedChild.Identity ||
                    parentSemanticsAfter != currentSemantics)
                {
                    throw Unknown($"Install path component '{component}' changed during observation.", expectedRoot);
                }

                edges.Add(new DirectoryEdgeObservation(
                    current,
                    currentIdentity,
                    currentSemantics,
                    component,
                    child,
                    openedChild.Identity,
                    childSemantics));
                current = child;
                currentIdentity = openedChild.Identity;
                currentSemantics = childSemantics;
            }

            var markerName = PackageInstallStore.CompletionMarkerFileName;
            var leafBefore = _files.InspectHandle(current);
            RequireDirectory(
                leafBefore,
                currentIdentity,
                expectedRoot,
                "The completed install directory changed before marker inspection.");
            RequireSameSemantics(current, currentSemantics, expectedRoot);
            var markerBefore = RequireCompletionMarker(
                _files.InspectChildNoFollow(current, markerName),
                expectedRoot,
                expectedIdentity: null);

            var markerHandle = _files.OpenFileChildNoFollow(current, markerName, FileAccess.Read);
            ownedHandles.Add(markerHandle);
            var openedMarker = RequireCompletionMarker(
                _files.InspectHandle(markerHandle), expectedRoot, markerBefore.Identity);
            var markerAfter = RequireCompletionMarker(
                _files.InspectChildNoFollow(current, markerName), expectedRoot, markerBefore.Identity);
            var heldLeafAfter = _files.InspectHandle(current);
            var leafSemanticsAfter = ObserveSemantics(current, expectedRoot);
            if (openedMarker.Identity != markerBefore.Identity || markerAfter.Identity != markerBefore.Identity ||
                heldLeafAfter.Kind != PhysicalStoreEntryKind.Directory || heldLeafAfter.Identity != currentIdentity ||
                leafSemanticsAfter != currentSemantics)
            {
                throw Unknown("The completion marker or install directory changed during observation.", expectedRoot);
            }

            var completionIdentity = ProtectionDigest.PackageInstallCompletionIdentity(openedMarker.Identity);
            var installIdentity = new PackageInstallIdentity(
                expectedRoot,
                packageId,
                version,
                rootRelativeInstallPath,
                currentIdentity,
                completionIdentity,
                alreadyVerifiedArchiveHash);
            var observation = new PackageInstallIdentityObservation(
                _files,
                _names,
                verifiedRoot,
                expectedRoot,
                rootInfo.Identity,
                rootSemantics,
                edges,
                current,
                currentIdentity,
                currentSemantics,
                markerHandle,
                openedMarker.Identity,
                installIdentity,
                ownedHandles);
            ownedHandles.Clear();
            try
            {
                observation.Revalidate();
                return observation;
            }
            catch
            {
                observation.Dispose();
                throw;
            }
        }
        catch
        {
            DisposeReverse(ownedHandles);
            throw;
        }
    }

    private static bool IsReservedRootDirectory(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        string requestedName,
        PhysicalFileIdentity directoryIdentity,
        PhysicalStoreNameSemantics rootSemantics)
    {
        foreach (var reservedName in ReservedRootDirectoryNames)
        {
            // Exact reserved names are refused directly. For case-insensitive profiles, resolving
            // the reserved spelling through the provider and comparing native identities detects
            // aliases without implementing or guessing a managed case-folding rule.
            if (rootSemantics.CaseSensitive && string.Equals(requestedName, reservedName, StringComparison.Ordinal))
                return true;

            var reserved = files.InspectChildNoFollow(root, reservedName);
            if (reserved is { Kind: PhysicalStoreEntryKind.Directory } && reserved.Identity == directoryIdentity)
                return true;
        }

        return false;
    }

    private PhysicalStoreNameSemantics ObserveSemantics(
        PhysicalStoreDirectoryHandle directory,
        PhysicalRootIdentity expectedRoot)
    {
        var semantics = _names.ObserveDirectoryNameSemantics(directory);
        var expectedEncoding = OperatingSystem.IsWindows()
            ? PhysicalStoreNameEncoding.Utf16LittleEndian
            : PhysicalStoreNameEncoding.Utf8;
        var supportedProfile = semantics.ProfileId switch
        {
            "windows-ntfs-name-v1" => OperatingSystem.IsWindows() && !semantics.NormalizationInsensitive,
            "darwin-apfs-v1" => OperatingSystem.IsMacOS(),
            "linux-ext4-sensitive-v1" => OperatingSystem.IsLinux() &&
                semantics.CaseSensitive && !semantics.NormalizationInsensitive,
            "linux-ext4-casefold-v1" => OperatingSystem.IsLinux() &&
                !semantics.CaseSensitive && semantics.NormalizationInsensitive,
            _ => false
        };
        if (semantics.Encoding != expectedEncoding || !supportedProfile)
            throw new PackageStoreAdmissionException(
                PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem returned an unsupported native name profile.",
                expectedRoot);
        return semantics;
    }

    private void RequireSameSemantics(
        PhysicalStoreDirectoryHandle directory,
        PhysicalStoreNameSemantics expected,
        PhysicalRootIdentity root)
    {
        if (ObserveSemantics(directory, root) != expected)
            throw Unknown("A package install path directory's native name profile changed.", root);
    }

    internal static string[] ValidateRelativeInstallPath(string path, string packageId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':') || path.Contains('\\') || path.Contains('\0'))
            throw new ArgumentException("The install path must be a normalized root-relative slash-separated path.", nameof(path));

        var components = path.Split('/', StringSplitOptions.None);
        if (components.Length != 3 || components.Any(static component => component is "" or "." or ".."))
            throw new ArgumentException("The install path must contain exactly feed, package, and version components.", nameof(path));
        foreach (var component in components)
            PhysicalStoreNames.ValidateSingleComponent(component);

        if (!string.Equals(components[1], packageId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The install path package component does not match the package identifier.", nameof(path));
        if (!NuGetVersion.TryParse(components[2], out var pathVersion) ||
            !NuGetVersion.TryParse(version, out var selectedVersion) ||
            !VersionComparer.VersionRelease.Equals(pathVersion, selectedVersion))
        {
            throw new ArgumentException("The install path version component does not match the selected NuGet version.", nameof(path));
        }

        return components;
    }

    private static PhysicalStoreEntryInfo RequireCompletionMarker(
        PhysicalStoreEntryInfo? info,
        PhysicalRootIdentity root,
        PhysicalFileIdentity? expectedIdentity)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 || info.Length != 0 ||
            (expectedIdentity is not null && info.Identity != expectedIdentity))
        {
            throw Unknown("A package install requires one unchanged, empty regular completion marker without hard-link ambiguity.", root);
        }
        RequireSameVolume(info.Identity, root.HandleIdentity, root);
        return info;
    }

    private static PhysicalStoreEntryInfo RequireDirectory(
        PhysicalStoreEntryInfo? info,
        PhysicalFileIdentity expectedIdentity,
        PhysicalRootIdentity root,
        string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.Directory || info.Identity != expectedIdentity)
            throw Unknown(message, root);
        RequireSameVolume(info.Identity, root.HandleIdentity, root);
        return info;
    }

    private static void RequireDirectoryEntry(
        PhysicalStoreEntryInfo? info,
        PhysicalRootIdentity root,
        string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown(message, root);
        RequireSameVolume(info.Identity, root.HandleIdentity, root);
    }

    private static void RequireSameVolume(
        PhysicalFileIdentity identity,
        PhysicalFileIdentity expectedRootIdentity,
        PhysicalRootIdentity root)
    {
        if (!string.Equals(identity.Provider, expectedRootIdentity.Provider, StringComparison.Ordinal) ||
            !string.Equals(identity.VolumeOrDeviceId, expectedRootIdentity.VolumeOrDeviceId, StringComparison.Ordinal))
        {
            throw Unknown("A package install component is on a different native provider or volume.", root);
        }
    }

    private static PackageStoreAdmissionException Unknown(string message, PhysicalRootIdentity? root)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private static void DisposeReverse(IReadOnlyList<PhysicalStoreHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
            handles[index].Dispose();
    }

    internal sealed record DirectoryEdgeObservation(
        PhysicalStoreDirectoryHandle Parent,
        PhysicalFileIdentity ParentIdentity,
        PhysicalStoreNameSemantics ParentSemantics,
        string RequestedName,
        PhysicalStoreDirectoryHandle Child,
        PhysicalFileIdentity ChildIdentity,
        PhysicalStoreNameSemantics ChildSemantics);

    /// <summary>Owns retained path and marker handles for one descriptive install identity.</summary>
    internal sealed class PackageInstallIdentityObservation : IDisposable
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly PhysicalStoreDirectoryHandle _root;
        private readonly PhysicalRootIdentity _expectedRoot;
        private readonly PhysicalFileIdentity _rootIdentity;
        private readonly PhysicalStoreNameSemantics _rootSemantics;
        private readonly IReadOnlyList<DirectoryEdgeObservation> _edges;
        private readonly PhysicalStoreDirectoryHandle _installDirectory;
        private readonly PhysicalFileIdentity _installDirectoryIdentity;
        private readonly PhysicalStoreNameSemantics _installDirectorySemantics;
        private readonly PhysicalStoreFileHandle _completionMarker;
        private readonly PhysicalFileIdentity _completionMarkerIdentity;
        private readonly IReadOnlyList<PhysicalStoreHandle> _ownedHandles;
        private readonly object _gate = new();
        private bool _disposed;
        private bool _preparedForMove;

        internal PackageInstallIdentityObservation(
            IPhysicalStoreFileSystem files,
            IPhysicalStoreNameFileSystem names,
            PhysicalStoreDirectoryHandle root,
            PhysicalRootIdentity expectedRoot,
            PhysicalFileIdentity rootIdentity,
            PhysicalStoreNameSemantics rootSemantics,
            IReadOnlyList<DirectoryEdgeObservation> edges,
            PhysicalStoreDirectoryHandle installDirectory,
            PhysicalFileIdentity installDirectoryIdentity,
            PhysicalStoreNameSemantics installDirectorySemantics,
            PhysicalStoreFileHandle completionMarker,
            PhysicalFileIdentity completionMarkerIdentity,
            PackageInstallIdentity installIdentity,
            IReadOnlyList<PhysicalStoreHandle> ownedHandles)
        {
            _files = files;
            _names = names;
            _root = root;
            _expectedRoot = expectedRoot;
            _rootIdentity = rootIdentity;
            _rootSemantics = rootSemantics;
            _edges = edges.ToArray();
            _installDirectory = installDirectory;
            _installDirectoryIdentity = installDirectoryIdentity;
            _installDirectorySemantics = installDirectorySemantics;
            _completionMarker = completionMarker;
            _completionMarkerIdentity = completionMarkerIdentity;
            _ownedHandles = ownedHandles.ToArray();
            InstallIdentity = installIdentity;
        }

        /// <summary>Gets the immutable candidate identity observed from native handles.</summary>
        internal PackageInstallIdentity InstallIdentity { get; }

        internal PhysicalStoreDirectoryHandle InstallDirectory => _installDirectory;

        /// <summary>Revalidates root-to-install edges, name profiles, directory identities, and the marker.</summary>
        /// <exception cref="ObjectDisposedException">The observation has been disposed.</exception>
        /// <exception cref="PackageStoreAdmissionException">A retained native observation changed or expired.</exception>
        internal void Revalidate()
        {
            lock (_gate)
            {
                RequireFullObservationLocked();
                RevalidateLocked();
            }
        }

        /// <summary>Joins every retained install-path edge to its exact canonical native name.</summary>
        /// <remarks>This deletion-only check leaves ordinary <see cref="Observe"/> behavior noncanonical.</remarks>
        internal PackageInstallDeletionLocation ObserveCanonicalDeletionLocation()
        {
            lock (_gate)
            {
                RequireFullObservationLocked();
                RevalidateLocked();
                return ObserveCanonicalDeletionLocationLocked();
            }
        }

        /// <summary>Closes leaf and marker handles while preserving exact root/feed/package ancestry.</summary>
        /// <remarks>This is a native-handle lifecycle transition only; it does not publish or move anything.</remarks>
        internal PackageInstallDeletionLocation PrepareForMove(PackageInstallDeletionLocation expectedLocation)
        {
            ArgumentNullException.ThrowIfNull(expectedLocation);
            lock (_gate)
            {
                RequireFullObservationLocked();
                RevalidateLocked();
                var observed = ObserveCanonicalDeletionLocationLocked();
                RequireSameDeletionLocation(expectedLocation, observed);
                RevalidateLocked();
                RequireCanonicalCompletionMarker();
                _completionMarker.Dispose();
                _installDirectory.Dispose();
                _preparedForMove = true;
                return observed;
            }
        }

        /// <summary>Revalidates retained root/feed/package ancestry after leaf handles have closed.</summary>
        internal void RevalidateDeletionParent()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_preparedForMove)
                    throw new InvalidOperationException("The install leaf must be closed before using its deletion parent binding.");
                RevalidateRootAndPackageParentLocked(canonical: true);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                DisposeReverse(_ownedHandles);
            }
        }

        private void RevalidateEdge(DirectoryEdgeObservation edge)
        {
            var parentBefore = _files.InspectHandle(edge.Parent);
            if (parentBefore.Kind != PhysicalStoreEntryKind.Directory || parentBefore.Identity != edge.ParentIdentity)
                throw Unknown("A retained install-path parent identity changed.", _expectedRoot);
            PackageInstallIdentityReader.RequireSameVolume(parentBefore.Identity, _rootIdentity, _expectedRoot);
            RequireSameSemantics(edge.Parent, edge.ParentSemantics);

            var namedBefore = PackageInstallIdentityReader.RequireDirectory(
                _files.InspectChildNoFollow(edge.Parent, edge.RequestedName),
                edge.ChildIdentity,
                _expectedRoot,
                "A retained install-path name no longer identifies its original directory.");
            using var reopened = _files.OpenDirectoryChildNoFollow(edge.Parent, edge.RequestedName);
            var reopenedInfo = _files.InspectHandle(reopened);
            PackageInstallIdentityReader.RequireDirectory(
                reopenedInfo,
                edge.ChildIdentity,
                _expectedRoot,
                "A retained install-path child changed during reopen.");
            var heldChild = _files.InspectHandle(edge.Child);
            PackageInstallIdentityReader.RequireDirectory(
                heldChild,
                edge.ChildIdentity,
                _expectedRoot,
                "A retained install-path child handle changed.");
            RequireSameSemantics(edge.Child, edge.ChildSemantics);
            var parentAfter = _files.InspectHandle(edge.Parent);
            PackageInstallIdentityReader.RequireDirectory(
                parentAfter,
                edge.ParentIdentity,
                _expectedRoot,
                "A retained install-path parent changed during revalidation.");
            var namedAfter = PackageInstallIdentityReader.RequireDirectory(
                _files.InspectChildNoFollow(edge.Parent, edge.RequestedName),
                edge.ChildIdentity,
                _expectedRoot,
                "A retained install-path name changed during revalidation.");
            RequireSameSemantics(edge.Parent, edge.ParentSemantics);
            if (namedBefore.Identity != reopenedInfo.Identity || namedAfter.Identity != reopenedInfo.Identity)
                throw Unknown("A retained install-path name no longer resolves to its opened child.", _expectedRoot);
        }

        private void RevalidateLocked()
        {
            RevalidateRootAndPackageParentLocked(canonical: false);
            var edge = _edges[^1];
            RevalidateEdge(edge);

            var installDirectory = _files.InspectHandle(_installDirectory);
            if (installDirectory.Kind != PhysicalStoreEntryKind.Directory ||
                installDirectory.Identity != _installDirectoryIdentity)
            {
                throw Unknown("The retained package install directory changed.", _expectedRoot);
            }
            PackageInstallIdentityReader.RequireSameVolume(installDirectory.Identity, _rootIdentity, _expectedRoot);
            RequireSameSemantics(_installDirectory, _installDirectorySemantics);

            var marker = PackageInstallIdentityReader.RequireCompletionMarker(
                _files.InspectHandle(_completionMarker), _expectedRoot, _completionMarkerIdentity);
            var namedMarker = PackageInstallIdentityReader.RequireCompletionMarker(
                _files.InspectChildNoFollow(_installDirectory, PackageInstallStore.CompletionMarkerFileName),
                _expectedRoot,
                _completionMarkerIdentity);
            using var reopenedMarker = _files.OpenFileChildNoFollow(
                _installDirectory,
                PackageInstallStore.CompletionMarkerFileName,
                FileAccess.Read);
            var reopened = PackageInstallIdentityReader.RequireCompletionMarker(
                _files.InspectHandle(reopenedMarker), _expectedRoot, _completionMarkerIdentity);
            var markerAfter = PackageInstallIdentityReader.RequireCompletionMarker(
                _files.InspectChildNoFollow(_installDirectory, PackageInstallStore.CompletionMarkerFileName),
                _expectedRoot,
                _completionMarkerIdentity);
            var leafAfter = _files.InspectHandle(_installDirectory);
            RequireSameSemantics(_installDirectory, _installDirectorySemantics);
            if (marker.Identity != namedMarker.Identity || reopened.Identity != marker.Identity ||
                markerAfter.Identity != marker.Identity || leafAfter.Kind != PhysicalStoreEntryKind.Directory ||
                leafAfter.Identity != _installDirectoryIdentity)
            {
                throw Unknown("The completion marker changed during revalidation.", _expectedRoot);
            }

            var completionIdentity = ProtectionDigest.PackageInstallCompletionIdentity(marker.Identity);
            if (!string.Equals(completionIdentity, InstallIdentity.CompletionIdentity, StringComparison.Ordinal))
                throw Unknown("The canonical completion identity changed.", _expectedRoot);
        }

        private void RevalidateRootAndPackageParentLocked(bool canonical)
        {
            var root = _files.InspectHandle(_root);
            if (root.Kind != PhysicalStoreEntryKind.Directory || root.Identity != _rootIdentity ||
                root.Identity != _expectedRoot.HandleIdentity)
            {
                throw Unknown("The retained package-install root identity changed.", _expectedRoot);
            }
            RequireSameSemantics(_root, _rootSemantics);

            if (_edges.Count != 0 && IsReservedRootDirectory(
                    _files,
                    _root,
                    _edges[0].RequestedName,
                    _edges[0].ChildIdentity,
                    _rootSemantics))
            {
                throw Unknown("The retained install path now resolves to a reserved control or staging directory.", _expectedRoot);
            }

            // The last edge names the install leaf. Once it closes, only root -> feed -> package
            // remains part of the held deletion parent binding.
            var retainedEdgeCount = _edges.Count - 1;
            for (var index = 0; index < retainedEdgeCount; index++)
            {
                var edge = _edges[index];
                RevalidateEdge(edge);
                if (canonical)
                    RequireCanonicalDirectoryEdge(edge);
            }
        }

        private PackageInstallDeletionLocation ObserveCanonicalDeletionLocationLocked()
        {
            for (var index = 0; index < _edges.Count; index++)
                RequireCanonicalDirectoryEdge(_edges[index]);

            var parent = _edges[^1].Parent;
            var parentInfo = _files.InspectHandle(parent);
            if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity != _edges[^1].ParentIdentity)
                throw Unknown("The exact package parent changed during canonical name observation.", _expectedRoot);
            RequireSameSemantics(parent, _edges[^1].ParentSemantics);
            RequireCanonicalCompletionMarker();
            return new PackageInstallDeletionLocation(
                _root,
                _rootIdentity,
                _rootSemantics,
                parent,
                _edges[^1].ParentIdentity,
                _edges[^1].ParentSemantics,
                _edges[^1].RequestedName,
                _installDirectoryIdentity,
                _installDirectorySemantics,
                _completionMarkerIdentity,
                InstallIdentity);
        }

        private void RequireCanonicalDirectoryEdge(DirectoryEdgeObservation edge)
        {
            var directories = _files as IPhysicalStoreDirectoryNameFileSystem
                ?? throw Unsupported("Deletion candidate binding requires exact native directory-name observations.");
            var canonical = directories.ObserveCanonicalDirectoryNameNoFollow(
                edge.Parent, edge.RequestedName, edge.ChildIdentity);
            if (canonical.Basename != edge.RequestedName || canonical.ParentIdentity != edge.ParentIdentity ||
                canonical.Semantics != edge.ParentSemantics || canonical.FileIdentity != edge.ChildIdentity)
            {
                throw Unknown("An install path component is not the exact canonical native directory entry.", _expectedRoot);
            }
        }

        private void RequireCanonicalCompletionMarker()
        {
            var canonical = _names.ObserveCanonicalFileNameNoFollow(
                _installDirectory,
                PackageInstallStore.CompletionMarkerFileName,
                _completionMarkerIdentity);
            if (canonical.Basename != PackageInstallStore.CompletionMarkerFileName ||
                canonical.ParentIdentity != _installDirectoryIdentity ||
                canonical.FileIdentity != _completionMarkerIdentity ||
                canonical.Semantics != _installDirectorySemantics)
            {
                throw Unknown("The completion marker is not the exact canonical native file entry.", _expectedRoot);
            }
        }

        private void RequireFullObservationLocked()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparedForMove)
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.ExpiredScope,
                    "The install leaf has already been closed for its deletion parent binding.",
                    _expectedRoot);
        }

        private static void RequireSameDeletionLocation(
            PackageInstallDeletionLocation expected,
            PackageInstallDeletionLocation observed)
        {
            if (!ReferenceEquals(expected.RootHandle, observed.RootHandle) ||
                expected.RootIdentity != observed.RootIdentity || expected.RootSemantics != observed.RootSemantics ||
                !ReferenceEquals(expected.PackageParentHandle, observed.PackageParentHandle) ||
                expected.PackageParentIdentity != observed.PackageParentIdentity ||
                expected.PackageParentSemantics != observed.PackageParentSemantics ||
                !string.Equals(expected.OriginalBasename, observed.OriginalBasename, StringComparison.Ordinal) ||
                expected.InstallDirectoryIdentity != observed.InstallDirectoryIdentity ||
                expected.InstallDirectorySemantics != observed.InstallDirectorySemantics ||
                expected.CompletionMarkerIdentity != observed.CompletionMarkerIdentity ||
                expected.InstallIdentity != observed.InstallIdentity)
            {
                throw Unknown("The canonical install location changed after candidate binding.", expected.InstallIdentity.Root);
            }
        }

        private void RequireSameSemantics(PhysicalStoreDirectoryHandle directory, PhysicalStoreNameSemantics expected)
        {
            var actual = _names.ObserveDirectoryNameSemantics(directory);
            if (actual != expected)
                throw Unknown("A retained install-path directory name profile changed.", _expectedRoot);
        }

        private static PackageStoreAdmissionException Unknown(string message, PhysicalRootIdentity root)
            => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);
    }

    internal sealed record PackageInstallDeletionLocation(
        PhysicalStoreDirectoryHandle RootHandle,
        PhysicalFileIdentity RootIdentity,
        PhysicalStoreNameSemantics RootSemantics,
        PhysicalStoreDirectoryHandle PackageParentHandle,
        PhysicalFileIdentity PackageParentIdentity,
        PhysicalStoreNameSemantics PackageParentSemantics,
        string OriginalBasename,
        PhysicalFileIdentity InstallDirectoryIdentity,
        PhysicalStoreNameSemantics InstallDirectorySemantics,
        PhysicalFileIdentity CompletionMarkerIdentity,
        PackageInstallIdentity InstallIdentity);
}
