using System.IO.Compression;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane;

/// <summary>
/// Reads files shipped inside an installed package's content, transparently handling both extracted install
/// directories and <c>.nupkg</c> archives. Hosts that need to inspect package-shipped files — manifests,
/// nuspecs, and similar — should use this rather than re-deriving the on-disk package layout themselves.
/// </summary>
public static class PackageContent
{
    private const int MaximumPackageRootEntries = 16_384;

    /// <summary>
    /// Reads the bytes of a package-relative file from an Unenrolled package directory or archive. Enrolled or
    /// uncertain package paths return <see langword="null"/> before any content bytes are read.
    /// </summary>
    /// <param name="installPath">The package install path — either an extracted directory or a <c>.nupkg</c> file.</param>
    /// <param name="relativePath">A forward-slash or backslash separated path within the package.</param>
    /// <returns>The file bytes, or <see langword="null"/> when content is missing or cannot safely be read.</returns>
    public static byte[]? TryReadFile(string installPath, string relativePath)
        => TryReadFileUnscoped(installPath, relativePath, PackageStoreRuntimeAdmission.CreatePhysicalFileSystem());

    /// <summary>Reads package content while a live operation borrow admits and protects the exact target path.</summary>
    /// <remarks>The caller retains ownership of <paramref name="borrow"/> and must keep it alive for this call.</remarks>
    public static byte[]? TryReadFile(
        string installPath,
        string relativePath,
        PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        if (!TryParseRelativePath(relativePath, out var components))
            return null;

        try
        {
            return PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
                borrow,
                installPath,
                (files, directory) => directory is null ? null : ReadDirectoryFile(files, directory, components));
        }
        catch (PackageStoreAdmissionException) when (IsNupkg(installPath))
        {
            return PackageStoreOperationAccess.WithValidatedPackageArchive(
                borrow,
                installPath,
                (files, parent, name, file) => ReadArchiveFile(files, parent, name, file,
                    archive => ReadArchiveEntry(archive, components)));
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Reads package content from an exact extracted install directory in a live graph-use lease.</summary>
    /// <remarks>The lease pins the exact install path until all package bytes have been detached.</remarks>
    public static byte[]? TryReadFile(
        string installPath,
        string relativePath,
        PackageGraphUseLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(lease);
        if (!TryParseRelativePath(relativePath, out var components))
            return null;

        try
        {
            return WithGraphInstallDirectory(installPath, lease,
                (files, directory) => ReadDirectoryFile(files, directory, components));
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first package-root file whose name ends with <paramref name="extension"/> (case-insensitive)
    /// in an Unenrolled package directory or archive. Enrolled or uncertain paths return null before content reads.
    /// </summary>
    public static PackageContentFile? TryFindByExtension(string installPath, string extension)
        => TryFindByExtensionUnscoped(installPath, extension, PackageStoreRuntimeAdmission.CreatePhysicalFileSystem());

    /// <summary>Finds a package-root file while a live operation borrow admits and protects the exact target path.</summary>
    public static PackageContentFile? TryFindByExtension(
        string installPath,
        string extension,
        PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        if (!IsValidExtension(extension))
            return null;

        try
        {
            return PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
                borrow,
                installPath,
                (files, directory) => directory is null ? null : FindDirectoryFileByExtension(files, directory, extension));
        }
        catch (PackageStoreAdmissionException) when (IsNupkg(installPath))
        {
            return PackageStoreOperationAccess.WithValidatedPackageArchive(
                borrow,
                installPath,
                (files, parent, name, file) => ReadArchiveFile(files, parent, name, file,
                    archive => FindArchiveFileByExtension(archive, extension)));
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Finds a package-root file in an exact extracted install directory protected by a graph-use lease.</summary>
    public static PackageContentFile? TryFindByExtension(
        string installPath,
        string extension,
        PackageGraphUseLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsValidExtension(extension))
            return null;

        try
        {
            return WithGraphInstallDirectory(installPath, lease,
                (files, directory) => FindDirectoryFileByExtension(files, directory, extension));
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Explicit-provider seam used by native tests to count target payload reads.</summary>
    internal static byte[]? TryReadFileUnscoped(
        string installPath,
        string relativePath,
        IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(installPath) || !TryParseRelativePath(relativePath, out var components))
            return null;

        try
        {
            return PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath,
                files,
                (nativeFiles, availability, directory) => availability == UnenrolledPackageDirectoryStatus.Missing
                    ? null
                    : ReadDirectoryFile(nativeFiles,
                        directory ?? throw Unknown("A present Unenrolled package path has no held directory."), components));
        }
        catch (PackageStoreAdmissionException) when (IsNupkg(installPath))
        {
            return TryReadUnenrolledArchive(installPath, files,
                archive => ReadArchiveEntry(archive, components));
        }
        catch (PackageStoreAdmissionException)
        {
            return null;
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Explicit-provider seam used by native tests to count target payload reads.</summary>
    internal static PackageContentFile? TryFindByExtensionUnscoped(
        string installPath,
        string extension,
        IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (string.IsNullOrWhiteSpace(installPath) || !IsValidExtension(extension))
            return null;

        try
        {
            return PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
                installPath,
                files,
                (nativeFiles, availability, directory) => availability == UnenrolledPackageDirectoryStatus.Missing
                    ? null
                    : FindDirectoryFileByExtension(nativeFiles,
                        directory ?? throw Unknown("A present Unenrolled package path has no held directory."), extension));
        }
        catch (PackageStoreAdmissionException) when (IsNupkg(installPath))
        {
            return TryReadUnenrolledArchive(installPath, files,
                archive => FindArchiveFileByExtension(archive, extension));
        }
        catch (PackageStoreAdmissionException)
        {
            return null;
        }
        catch (Exception exception) when (IsContentReadFailure(exception))
        {
            return null;
        }
    }

    private static T? TryReadUnenrolledArchive<T>(string installPath, IPhysicalStoreFileSystem files, Func<ZipArchive, T?> read)
    {
        try
        {
            return PackageStoreRuntimeAdmission.WithUnenrolledPackageArchive(
                installPath,
                files,
                (nativeFiles, parent, name, file) => ReadArchiveFile(nativeFiles, parent, name, file, read));
        }
        catch (Exception exception) when (IsContentReadFailure(exception) || exception is PackageStoreAdmissionException)
        {
            return default;
        }
    }

    private static T WithGraphInstallDirectory<T>(
        string installPath,
        PackageGraphUseLease lease,
        Func<IPhysicalStoreFileSystem, PhysicalStoreDirectoryHandle, T> read)
    {
        using var pin = lease.AcquireRead(installPath);
        var expectedInstall = lease.GetInstallIdentityForExactPath(installPath);
        var nativeFiles = (lease.Control as PackageGraphUseLeaseOwnerControl)?.NativeFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "The graph-use lease does not retain its original native filesystem provider.", expectedInstall.Root);
        var resolver = new PackageStoreAuthorityResolver(nativeFiles,
            new RootMembershipRegistry(nativeFiles, new Nuplane.Store.State.StoreStateSerializer()));
        using var resolved = resolver.ResolveRetainedInstallPath(installPath, expectedInstall);
        var root = resolved.AuthorityRoot
            ?? throw Unknown("The retained graph-use path has no held authority root.", expectedInstall.Root);
        if (resolved.RootIdentity != expectedInstall.Root)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                "The retained graph-use path resolved to a different physical root.", expectedInstall.Root);

        using var observation = new PackageInstallIdentityReader(nativeFiles).Observe(
            root,
            expectedInstall.Root,
            expectedInstall.RootRelativeInstallPath,
            expectedInstall.PackageId,
            expectedInstall.Version,
            expectedInstall.VerifiedArchiveHash);
        var directory = resolved.Target as PhysicalStoreDirectoryHandle
            ?? throw Unknown("The retained graph-use path is not a held install directory.", expectedInstall.Root);
        var targetInfo = nativeFiles.InspectHandle(directory);
        if (observation.InstallIdentity != expectedInstall ||
            targetInfo.Kind != PhysicalStoreEntryKind.Directory ||
            targetInfo.Identity != observation.InstallIdentity.DirectoryIdentity)
        {
            throw Unknown("The retained graph-use install no longer matches its published native identity.", expectedInstall.Root);
        }

        resolved.Revalidate();
        observation.Revalidate();
        var result = read(nativeFiles, observation.InstallDirectory);
        observation.Revalidate();
        resolved.Revalidate();
        return result;
    }

    private static byte[]? ReadDirectoryFile(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle packageDirectory,
        IReadOnlyList<string> components)
    {
        var names = RequireNames(files);
        var ownedDirectories = new List<PhysicalStoreDirectoryHandle>();
        var current = packageDirectory;
        try
        {
            for (var index = 0; index < components.Count; index++)
            {
                var name = components[index];
                var parentBefore = RequireDirectory(files.InspectHandle(current), "A package content parent is not a directory.");
                var semanticsBefore = names.ObserveDirectoryNameSemantics(current);
                var child = files.InspectChildNoFollow(current, name);
                if (child is null)
                {
                    RequireStableMissingChild(files, names, current, parentBefore, semanticsBefore, name);
                    return null;
                }

                var isLast = index == components.Count - 1;
                if (!isLast)
                {
                    if (child.Kind != PhysicalStoreEntryKind.Directory)
                        throw Unknown("A package content path component is not an ordinary directory.");
                    RequireSameVolume(parentBefore.Identity, child.Identity);
                    var openedDirectory = files.OpenDirectoryChildNoFollow(current, name);
                    ownedDirectories.Add(openedDirectory);
                    var opened = RequireDirectory(files.InspectHandle(openedDirectory), "A package content directory changed kind while opening.");
                    if (opened.Identity != child.Identity)
                        throw Unknown("A package content directory changed between no-follow inspection and open.");
                    var childSemantics = names.ObserveDirectoryNameSemantics(openedDirectory);
                    var openedNameAfter = files.InspectChildNoFollow(current, name);
                    RequireStableDirectoryEdge(files, names, current, openedDirectory, parentBefore, semanticsBefore,
                        name, child.Identity, childSemantics, openedNameAfter);
                    current = openedDirectory;
                    continue;
                }

                if (child.Kind != PhysicalStoreEntryKind.RegularFile || child.LinkCount != 1)
                    throw Unknown("Package content must be an ordinary single-link file without a final alias.");
                RequireSameVolume(parentBefore.Identity, child.Identity);
                var canonicalBefore = names.ObserveCanonicalFileNameNoFollow(current, name, child.Identity);
                RequireCanonicalEdge(canonicalBefore, parentBefore, child);
                using var file = files.OpenFileChildNoFollow(current, name, FileAccess.Read);
                var openedFile = files.InspectHandle(file);
                if (openedFile.Kind != PhysicalStoreEntryKind.RegularFile || openedFile.LinkCount != 1 ||
                    openedFile.Identity != child.Identity || openedFile.Length != child.Length)
                    throw Unknown("Package content changed between no-follow inspection and open.");
                if (openedFile.Length > Array.MaxLength)
                    throw new InvalidDataException("Package content is larger than a managed byte array can represent.");

                var bytes = files.ReadControlFile(file, Array.MaxLength);
                var heldAfter = files.InspectHandle(file);
                var namedAfter = files.InspectChildNoFollow(current, name);
                var canonicalAfter = names.ObserveCanonicalFileNameNoFollow(current, name, openedFile.Identity);
                if (heldAfter.Kind != PhysicalStoreEntryKind.RegularFile || heldAfter.LinkCount != 1 ||
                    heldAfter.Identity != openedFile.Identity || heldAfter.Length != openedFile.Length ||
                    bytes.LongLength != openedFile.Length || namedAfter is null ||
                    namedAfter.Kind != PhysicalStoreEntryKind.RegularFile || namedAfter.LinkCount != 1 ||
                    namedAfter.Identity != openedFile.Identity || namedAfter.Length != openedFile.Length ||
                    canonicalAfter != canonicalBefore)
                    throw Unknown("Package content changed while its detached bytes were read.");
                RequireStableDirectory(files, names, current, parentBefore, semanticsBefore);
                return bytes;
            }

            return null;
        }
        finally
        {
            for (var index = ownedDirectories.Count - 1; index >= 0; index--)
                ownedDirectories[index].Dispose();
        }
    }

    private static PackageContentFile? FindDirectoryFileByExtension(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle packageDirectory,
        string extension)
    {
        var names = RequireNames(files);
        var enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot enumerate package content through a held native directory.");
        var before = RequireDirectory(files.InspectHandle(packageDirectory), "The package content root is not a directory.");
        var semantics = names.ObserveDirectoryNameSemantics(packageDirectory);
        var childNames = enumeration.EnumerateChildNamesNoFollow(packageDirectory, MaximumPackageRootEntries);
        foreach (var name in childNames)
        {
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryParseRelativePath(name, out var components) || components.Length != 1)
                throw Unknown("The native directory enumeration returned an unsupported package file name.");
            if (files.InspectChildNoFollow(packageDirectory, name) is null)
                throw Unknown("A package file disappeared after native directory enumeration.");
            var bytes = ReadDirectoryFile(files, packageDirectory, components);
            if (bytes is null)
                throw Unknown("A matching package file disappeared during native content reading.");
            RequireStableDirectory(files, names, packageDirectory, before, semantics);
            return new PackageContentFile(name, bytes);
        }

        RequireStableDirectory(files, names, packageDirectory, before, semantics);
        return null;
    }

    private static T? ReadArchiveFile<T>(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent,
        string name,
        PhysicalStoreFileHandle file,
        Func<ZipArchive, T?> read)
    {
        var names = RequireNames(files);
        var streams = files as IPhysicalStorePackageStreamFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot read a package archive through held native handles.");
        var parentInfo = RequireDirectory(files.InspectHandle(parent), "The package archive parent is not a directory.");
        var fileInfo = files.InspectHandle(file);
        if (fileInfo.Kind != PhysicalStoreEntryKind.RegularFile || fileInfo.LinkCount != 1)
            throw Unknown("A package archive must be one ordinary single-link file.");
        RequireSameVolume(parentInfo.Identity, fileInfo.Identity);
        var canonical = names.ObserveCanonicalFileNameNoFollow(parent, name, fileInfo.Identity);
        if (canonical.ParentIdentity != parentInfo.Identity || canonical.FileIdentity != fileInfo.Identity)
            throw Unknown("The package archive name does not identify its held parent and file.");

        using var stream = streams.OpenPackageArchiveReadStream(
            parent, canonical.Basename, file, parentInfo, fileInfo, fileInfo.Length);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var result = read(archive);
        var parentAfter = RequireDirectory(files.InspectHandle(parent), "The package archive parent changed kind.");
        var fileAfter = files.InspectHandle(file);
        var nameAfter = files.InspectChildNoFollow(parent, canonical.Basename);
        var canonicalAfter = names.ObserveCanonicalFileNameNoFollow(parent, canonical.Basename, fileInfo.Identity);
        if (parentAfter.Identity != parentInfo.Identity || fileAfter.Kind != PhysicalStoreEntryKind.RegularFile ||
            fileAfter.LinkCount != 1 || fileAfter.Identity != fileInfo.Identity || fileAfter.Length != fileInfo.Length ||
            nameAfter is null || nameAfter.Kind != PhysicalStoreEntryKind.RegularFile || nameAfter.LinkCount != 1 ||
            nameAfter.Identity != fileInfo.Identity || canonicalAfter != canonical)
            throw Unknown("The package archive changed while its entries were read.");
        return result;
    }

    private static byte[]? ReadArchiveEntry(ZipArchive archive, IReadOnlyList<string> components)
    {
        var requested = string.Join('/', components);
        var entry = FindUniqueArchiveEntry(archive, name =>
            string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ReadEntry(entry);
    }

    private static PackageContentFile? FindArchiveFileByExtension(ZipArchive archive, string extension)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? firstMatch = null;
        string? firstName = null;
        foreach (var entry in archive.Entries)
        {
            if (!TryNormalizeArchiveEntry(entry.FullName, out var name) || name.Contains('/') ||
                !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(name))
                return null;
            if (firstMatch is null)
            {
                firstMatch = entry;
                firstName = name;
            }
        }
        return firstMatch is null ? null : new PackageContentFile(firstName!, ReadEntry(firstMatch));
    }

    private static ZipArchiveEntry? FindUniqueArchiveEntry(ZipArchive archive, Func<string, bool> matches)
    {
        ZipArchiveEntry? match = null;
        string? matchedName = null;
        foreach (var entry in archive.Entries)
        {
            if (!TryNormalizeArchiveEntry(entry.FullName, out var name) || !matches(name))
                continue;
            if (match is not null && string.Equals(matchedName, name, StringComparison.OrdinalIgnoreCase))
                return null;
            if (match is null)
            {
                match = entry;
                matchedName = name;
            }
        }
        return match;
    }

    private static bool TryNormalizeArchiveEntry(string entryName, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(entryName))
            return false;
        var path = entryName.Replace('\\', '/');
        if (path.EndsWith("/", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains(':') || path.Contains('\0') || Path.IsPathRooted(path))
            return false;
        var components = path.Split('/');
        try
        {
            foreach (var component in components)
                PhysicalStoreNames.ValidateSingleComponent(component);
        }
        catch (ArgumentException)
        {
            return false;
        }
        normalized = string.Join('/', components);
        return true;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > Array.MaxLength)
            throw new InvalidDataException("Package archive content is larger than a managed byte array can represent.");

        var bytes = new byte[(int)entry.Length];
        using var entryStream = entry.Open();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = entryStream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidDataException("Package archive content ended before its declared length.");
            offset += read;
        }

        if (entryStream.ReadByte() != -1)
            throw new InvalidDataException("Package archive content exceeded its declared length.");
        return bytes;
    }

    private static bool TryParseRelativePath(string? relativePath, out string[] components)
    {
        components = [];
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':') ||
            normalized.Contains('\0') || Path.IsPathRooted(normalized))
            return false;
        var candidate = normalized.Split('/');
        try
        {
            foreach (var component in candidate)
                PhysicalStoreNames.ValidateSingleComponent(component);
        }
        catch (ArgumentException)
        {
            return false;
        }
        components = candidate;
        return true;
    }

    private static bool IsValidExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && extension.IndexOfAny(['/', '\\', ':', '\0']) < 0;

    private static bool IsNupkg(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            return false;
        try { return string.Equals(Path.GetExtension(path), ".nupkg", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsContentReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException;

    private static IPhysicalStoreNameFileSystem RequireNames(IPhysicalStoreFileSystem files) =>
        files as IPhysicalStoreNameFileSystem
        ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
            "The filesystem cannot verify native package content names.");

    private static PhysicalStoreEntryInfo RequireDirectory(PhysicalStoreEntryInfo info, string message)
    {
        if (info.Kind != PhysicalStoreEntryKind.Directory)
            throw Unknown(message);
        return info;
    }

    private static void RequireStableMissingChild(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        PhysicalStoreEntryInfo before,
        PhysicalStoreNameSemantics semantics,
        string name)
    {
        var after = RequireDirectory(files.InspectHandle(parent), "A package content parent changed kind during missing-name observation.");
        var afterSemantics = names.ObserveDirectoryNameSemantics(parent);
        var namedAfter = files.InspectChildNoFollow(parent, name);
        if (after.Identity != before.Identity || afterSemantics != semantics || namedAfter is not null)
            throw Unknown("A missing package content entry changed during native absence observation.");
    }

    private static void RequireStableDirectoryEdge(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle parent,
        PhysicalStoreDirectoryHandle child,
        PhysicalStoreEntryInfo parentBefore,
        PhysicalStoreNameSemantics semanticsBefore,
        string name,
        PhysicalFileIdentity expectedChildIdentity,
        PhysicalStoreNameSemantics childSemantics,
        PhysicalStoreEntryInfo? namedAfter)
    {
        var parentAfter = RequireDirectory(files.InspectHandle(parent), "A package content parent changed kind.");
        var semanticsAfter = names.ObserveDirectoryNameSemantics(parent);
        var childAfter = RequireDirectory(files.InspectHandle(child), "A package content directory changed kind.");
        if (parentAfter.Identity != parentBefore.Identity || semanticsAfter != semanticsBefore ||
            namedAfter is null || namedAfter.Kind != PhysicalStoreEntryKind.Directory ||
            namedAfter.Identity != expectedChildIdentity || childAfter.Identity != expectedChildIdentity ||
            names.ObserveDirectoryNameSemantics(child) != childSemantics)
            throw Unknown("A package content directory edge changed during native traversal.");
    }

    private static void RequireStableDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directory,
        PhysicalStoreEntryInfo expected,
        PhysicalStoreNameSemantics semantics)
    {
        var after = RequireDirectory(files.InspectHandle(directory), "A package content directory changed kind.");
        if (after.Identity != expected.Identity || names.ObserveDirectoryNameSemantics(directory) != semantics)
            throw Unknown("A package content directory or native lookup profile changed during the read.");
    }

    private static void RequireCanonicalEdge(
        PhysicalStoreCanonicalName canonical,
        PhysicalStoreEntryInfo parent,
        PhysicalStoreEntryInfo file)
    {
        if (canonical.ParentIdentity != parent.Identity || canonical.FileIdentity != file.Identity)
            throw Unknown("A package content file name does not identify its held parent and file.");
    }

    private static void RequireSameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
    {
        if (!string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) ||
            !string.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Unknown("A package content entry is not on its package's physical volume.");
    }

    private static PackageStoreAdmissionException Unknown(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);
}

/// <summary>A file read from a package's content: its name and raw bytes.</summary>
/// <param name="Name">The file name (without directory).</param>
/// <param name="Content">The file bytes.</param>
public sealed record PackageContentFile(string Name, byte[] Content);
