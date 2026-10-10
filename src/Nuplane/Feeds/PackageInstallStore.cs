using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Registration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Feeds;

/// <summary>
/// Owns the on-disk layout of extracted packages and the rules for publishing an extraction into it.
/// Every feed kind — local directory and remote alike — extracts under the single writable root
/// derived from <see cref="FeedResolutionOptions.PackageInstallRoot"/>, so a feed directory that only
/// supplies <c>.nupkg</c> files never has to be writable and can be mounted read-only.
/// An install directory only counts as usable once it carries the completion marker, so a partially
/// extracted directory is never handed to the loader.
/// </summary>
internal static class PackageInstallStore
{
    private const int ContentHashFileByteCount = 95;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// The marker file written as the last step of an extraction, inside the staging directory and
    /// therefore already present when the directory is published under its final name.
    /// </summary>
    public const string CompletionMarkerFileName = ".nuplane-ready";

    /// <summary>The canonical hash of the exact archive used for a completed extraction.</summary>
    public const string ContentHashFileName = ".nuplane-content-hash";

    /// <summary>
    /// Resolves the writable root that all package extractions land under.
    /// </summary>
    /// <param name="options">The feed resolution options carrying the configured root.</param>
    public static string ResolveInstallRoot(FeedResolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !string.IsNullOrWhiteSpace(options.PackageInstallRoot)
            ? Path.GetFullPath(options.PackageInstallRoot)
            : Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages");
    }

    /// <summary>
    /// Composes the install directory for a concrete package version acquired from a feed.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="feedName">The name of the feed the package came from.</param>
    /// <param name="packageId">The package identifier to key the directory off.</param>
    /// <param name="version">The concrete package version.</param>
    public static string GetInstallDirectory(string installRoot, string feedName, string packageId, string version) =>
        Path.Combine(
            installRoot,
            SanitizePathSegment(feedName),
            SanitizePathSegment(packageId),
            SanitizePathSegment(version));

    /// <summary>
    /// Determines whether the install directory holds a completed extraction.
    /// </summary>
    /// <param name="installDirectory">The install directory to probe.</param>
    public static bool IsInstalled(string installDirectory)
        => PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
            installDirectory,
            (files, status, directory) => status == UnenrolledPackageDirectoryStatus.Missing
                ? false
                : IsInstalledNative(files,
                    directory ?? throw Refuse("A present install path did not provide a held native directory."),
                    expectedRoot: null));

    /// <summary>Checks for one native completion marker under the exact live admitted operation.</summary>
    /// <remarks>The borrow protects only this probe. Callers retaining package paths need an explicit graph-use lease.</remarks>
    public static bool IsInstalled(string installDirectory, PackageStoreOperationBorrow borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        return PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
            borrow,
            installDirectory,
            (files, directory) => directory is null
                ? false
                : IsInstalledNative(files, directory, borrow.Root));
    }

    /// <summary>
    /// Creates the staging area and returns a unique path inside it; the path itself is not created.
    /// Staging sits under the install root so that publishing an extraction is a rename within one
    /// volume.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="extension">An optional file extension for staged files.</param>
    public static string CreateStagingPath(string installRoot, string extension = "")
    {
        var stagingRoot = Path.Combine(installRoot, ".tmp");
        Directory.CreateDirectory(stagingRoot);
        return Path.Combine(stagingRoot, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Extracts a <c>.nupkg</c> into <paramref name="installDirectory"/>, staging the extraction under
    /// the install root and publishing it with a single rename so no half-extracted directory is ever
    /// reachable under the final path. The source <c>.nupkg</c> is only read.
    /// </summary>
    /// <param name="installRoot">The writable install root.</param>
    /// <param name="installDirectory">The final install directory, below <paramref name="installRoot"/>.</param>
    /// <param name="nupkgPath">The package file to extract.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task InstallAsync(
        string installRoot,
        string installDirectory,
        string nupkgPath,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = CreateStagingPath(installRoot);

        try
        {
            await using var packageStream = File.OpenRead(nupkgPath);
            var hash = await SHA512.HashDataAsync(packageStream, cancellationToken);
            var contentHash = "sha512:" + Convert.ToBase64String(hash);
            packageStream.Position = 0;

            Directory.CreateDirectory(stagingDirectory);
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                archive.ExtractToDirectory(stagingDirectory, overwriteFiles: true);
            }
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, ContentHashFileName),
                contentHash,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, CompletionMarkerFileName),
                string.Empty,
                cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);

            if (TryPublish(stagingDirectory, installDirectory))
            {
                stagingDirectory = string.Empty;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDirectory) && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Reads a previously acquired archive hash. Legacy installs and invalid or noncanonical
    /// metadata return <see langword="null"/>; extracted content cannot reconstruct archive bytes.
    /// </summary>
    /// <param name="installDirectory">The completed package install directory.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task<string?> ReadContentHashAsync(string installDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = PackageStoreRuntimeAdmission.WithUnenrolledPackageDirectory(
            installDirectory,
            (files, status, directory) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return status == UnenrolledPackageDirectoryStatus.Missing
                    ? null
                    : ReadContentHashNative(files,
                        directory ?? throw Refuse("A present install path did not provide a held native directory."),
                        expectedRoot: null,
                        cancellationToken);
            });
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.FromResult(contentHash).ConfigureAwait(false);
    }

    /// <summary>Reads one bounded canonical archive hash under the exact live admitted operation.</summary>
    /// <remarks>The borrow protects only this read. Callers retaining package paths need an explicit graph-use lease.</remarks>
    public static async Task<string?> ReadContentHashAsync(
        string installDirectory,
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(borrow);
        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = PackageStoreOperationAccess.WithValidatedPackageDirectoryOrMissing(
            borrow,
            installDirectory,
            (files, directory) => directory is null
                ? null
                : ReadContentHashNative(files, directory, borrow.Root, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.FromResult(contentHash).ConfigureAwait(false);
    }

    private static bool IsInstalledNative(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle installDirectory,
        PhysicalRootIdentity? expectedRoot)
    {
        var names = RequireNameFileSystem(files, expectedRoot);
        var directory = ObserveInstallDirectory(files, names, installDirectory, expectedRoot);
        var marker = ObserveOptionalRegularFile(files, names, installDirectory, directory,
            CompletionMarkerFileName, expectedRoot);
        if (marker is null)
        {
            RevalidateAbsentEntry(files, names, installDirectory, directory,
                CompletionMarkerFileName, expectedRoot);
            return false;
        }

        RequireFileMetadata(marker, 0, directory.Entry.Identity, expectedRoot,
            "The completion marker must be empty, regular, single-link, and on the install volume.");
        using var handle = files.OpenFileChildNoFollow(installDirectory, CompletionMarkerFileName, FileAccess.Read);
        var opened = RequireFileMetadata(files.InspectHandle(handle), 0, directory.Entry.Identity,
            expectedRoot, "The completion marker changed before it was opened.");
        RequireSameFile(marker, opened, expectedRoot, CompletionMarkerFileName);
        var namedAfterOpen = RequireFileMetadata(files.InspectChildNoFollow(installDirectory, CompletionMarkerFileName),
            0, directory.Entry.Identity, expectedRoot,
            "The completion marker changed while it was opened.");
        RequireSameFile(opened, namedAfterOpen, expectedRoot, CompletionMarkerFileName);
        RequireCanonicalName(names, installDirectory, CompletionMarkerFileName, opened.Identity, directory, expectedRoot);
        RevalidatePresentEntry(files, names, installDirectory, directory, CompletionMarkerFileName,
            opened, expectedRoot);
        return true;
    }

    private static string? ReadContentHashNative(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle installDirectory,
        PhysicalRootIdentity? expectedRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = RequireNameFileSystem(files, expectedRoot);
        var directory = ObserveInstallDirectory(files, names, installDirectory, expectedRoot);
        var hashFile = ObserveOptionalRegularFile(files, names, installDirectory, directory,
            ContentHashFileName, expectedRoot);
        if (hashFile is null)
        {
            RevalidateAbsentEntry(files, names, installDirectory, directory,
                ContentHashFileName, expectedRoot);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (hashFile.Length != ContentHashFileByteCount)
        {
            RequireCanonicalName(names, installDirectory, ContentHashFileName, hashFile.Identity, directory, expectedRoot);
            RevalidatePresentEntry(files, names, installDirectory, directory, ContentHashFileName,
                hashFile, expectedRoot);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        using var handle = files.OpenFileChildNoFollow(installDirectory, ContentHashFileName, FileAccess.Read);
        var opened = RequireFileMetadata(files.InspectHandle(handle), ContentHashFileByteCount, directory.Entry.Identity,
            expectedRoot, "The archive hash changed before it was opened.");
        RequireSameFile(hashFile, opened, expectedRoot, ContentHashFileName);
        RequireCanonicalName(names, installDirectory, ContentHashFileName, opened.Identity, directory, expectedRoot);

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = files.ReadControlFile(handle, ContentHashFileByteCount);
        cancellationToken.ThrowIfCancellationRequested();

        var heldAfterRead = RequireFileMetadata(files.InspectHandle(handle), ContentHashFileByteCount,
            directory.Entry.Identity, expectedRoot, "The archive hash changed during its bounded read.");
        var namedAfterRead = RequireFileMetadata(files.InspectChildNoFollow(installDirectory, ContentHashFileName),
            ContentHashFileByteCount, directory.Entry.Identity, expectedRoot,
            "The archive hash name changed during its bounded read.");
        RequireSameFile(opened, heldAfterRead, expectedRoot, ContentHashFileName);
        RequireSameFile(opened, namedAfterRead, expectedRoot, ContentHashFileName);
        RequireCanonicalName(names, installDirectory, ContentHashFileName, opened.Identity, directory, expectedRoot);
        RevalidateDirectory(files, names, installDirectory, directory, expectedRoot);
        if (bytes.Length != ContentHashFileByteCount)
            throw Refuse("The archive hash read did not return its exact bounded byte count.", expectedRoot);

        string contentHash;
        try
        {
            contentHash = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        const string prefix = "sha512:";
        if (contentHash.Length != prefix.Length + 88 || !contentHash.StartsWith(prefix, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        var decoded = new byte[64];
        var base64 = contentHash[prefix.Length..];
        var isCanonical = Convert.TryFromBase64String(base64, decoded, out var bytesWritten)
            && bytesWritten == decoded.Length
            && string.Equals(base64, Convert.ToBase64String(decoded), StringComparison.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return isCanonical ? contentHash : null;
    }

    private static IPhysicalStoreNameFileSystem RequireNameFileSystem(
        IPhysicalStoreFileSystem files,
        PhysicalRootIdentity? expectedRoot)
        => files as IPhysicalStoreNameFileSystem
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The filesystem cannot verify native package metadata names.", expectedRoot);

    private static InstallDirectoryObservation ObserveInstallDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle installDirectory,
        PhysicalRootIdentity? expectedRoot)
    {
        var directory = RequireDirectory(files.InspectHandle(installDirectory), expectedRoot);
        var semantics = RequireSupportedNameProfile(names.ObserveDirectoryNameSemantics(installDirectory), expectedRoot);
        var afterSemantics = RequireDirectory(files.InspectHandle(installDirectory), expectedRoot);
        if (afterSemantics.Identity != directory.Identity)
            throw Refuse("The held package install directory changed during profile observation.", expectedRoot);
        if (expectedRoot is not null)
            RequireSameVolume(directory.Identity, expectedRoot.HandleIdentity, expectedRoot,
                "The install directory is not on the admitted root's native volume.");
        return new InstallDirectoryObservation(directory, semantics);
    }

    private static PhysicalStoreEntryInfo? ObserveOptionalRegularFile(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle installDirectory,
        InstallDirectoryObservation directory,
        string name,
        PhysicalRootIdentity? expectedRoot)
    {
        PhysicalStoreNames.ValidateSingleComponent(name);
        var entry = files.InspectChildNoFollow(installDirectory, name);
        if (entry is null)
            return null;

        var validated = RequireFileMetadata(entry, null, directory.Entry.Identity, expectedRoot,
            $"The package metadata entry '{name}' is not a single-link regular file on the install volume.");
        RequireCanonicalName(names, installDirectory, name, validated.Identity, directory, expectedRoot);
        return validated;
    }

    private static PhysicalStoreEntryInfo RequireDirectory(
        PhysicalStoreEntryInfo? actual,
        PhysicalRootIdentity? expectedRoot)
    {
        if (actual is null || actual.Kind != PhysicalStoreEntryKind.Directory)
            throw Refuse("The held package install target is no longer a directory.", expectedRoot);
        return actual;
    }

    private static PhysicalStoreEntryInfo RequireFileMetadata(
        PhysicalStoreEntryInfo? actual,
        long? expectedLength,
        PhysicalFileIdentity expectedDirectory,
        PhysicalRootIdentity? expectedRoot,
        string message)
    {
        if (actual is null || actual.Kind != PhysicalStoreEntryKind.RegularFile || actual.LinkCount != 1 ||
            (expectedLength is not null && actual.Length != expectedLength.Value))
            throw Refuse(message, expectedRoot);
        RequireSameVolume(actual.Identity, expectedDirectory, expectedRoot, message);
        if (expectedRoot is not null)
            RequireSameVolume(actual.Identity, expectedRoot.HandleIdentity, expectedRoot, message);
        return actual;
    }

    private static void RequireSameFile(
        PhysicalStoreEntryInfo expected,
        PhysicalStoreEntryInfo actual,
        PhysicalRootIdentity? root,
        string name)
    {
        if (expected.Kind != actual.Kind || expected.Identity != actual.Identity ||
            expected.LinkCount != actual.LinkCount || expected.Length != actual.Length)
            throw Refuse($"The package metadata entry '{name}' changed during its native read.", root);
    }

    private static void RequireCanonicalName(
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directoryHandle,
        string name,
        PhysicalFileIdentity expectedFileIdentity,
        InstallDirectoryObservation directory,
        PhysicalRootIdentity? expectedRoot)
    {
        var canonical = names.ObserveCanonicalFileNameNoFollow(directoryHandle, name, expectedFileIdentity);
        if (!string.Equals(canonical.Basename, name, StringComparison.Ordinal) ||
            canonical.ParentIdentity != directory.Entry.Identity || canonical.FileIdentity != expectedFileIdentity ||
            canonical.Semantics != directory.Semantics)
            throw Refuse($"The package metadata entry '{name}' is not its exact native canonical name/profile.", expectedRoot);
    }

    private static void RevalidateAbsentEntry(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directoryHandle,
        InstallDirectoryObservation expectedDirectory,
        string name,
        PhysicalRootIdentity? expectedRoot)
    {
        RevalidateDirectory(files, names, directoryHandle, expectedDirectory, expectedRoot);
        if (files.InspectChildNoFollow(directoryHandle, name) is not null)
            throw Refuse($"The package metadata entry '{name}' appeared during its native probe.", expectedRoot);
        RevalidateDirectory(files, names, directoryHandle, expectedDirectory, expectedRoot);
    }

    private static void RevalidatePresentEntry(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directoryHandle,
        InstallDirectoryObservation expectedDirectory,
        string name,
        PhysicalStoreEntryInfo expectedEntry,
        PhysicalRootIdentity? expectedRoot)
    {
        RevalidateDirectory(files, names, directoryHandle, expectedDirectory, expectedRoot);
        var current = RequireFileMetadata(files.InspectChildNoFollow(directoryHandle, name), expectedEntry.Length,
            expectedDirectory.Entry.Identity, expectedRoot, $"The package metadata entry '{name}' changed during its probe.");
        RequireSameFile(expectedEntry, current, expectedRoot, name);
        RequireCanonicalName(names, directoryHandle, name, expectedEntry.Identity, expectedDirectory, expectedRoot);
        RevalidateDirectory(files, names, directoryHandle, expectedDirectory, expectedRoot);
    }

    private static void RevalidateDirectory(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreNameFileSystem names,
        PhysicalStoreDirectoryHandle directoryHandle,
        InstallDirectoryObservation expectedDirectory,
        PhysicalRootIdentity? expectedRoot)
    {
        var actual = RequireDirectory(files.InspectHandle(directoryHandle), expectedRoot);
        if (actual.Identity != expectedDirectory.Entry.Identity)
            throw Refuse("The held package install directory changed during its native metadata read.", expectedRoot);
        RequireSupportedNameProfile(names.ObserveDirectoryNameSemantics(directoryHandle), expectedRoot,
            expectedDirectory.Semantics);
        if (expectedRoot is not null)
            RequireSameVolume(actual.Identity, expectedRoot.HandleIdentity, expectedRoot,
                "The install directory left the admitted root's native volume.");
    }

    private sealed record InstallDirectoryObservation(
        PhysicalStoreEntryInfo Entry,
        PhysicalStoreNameSemantics Semantics);

    private static PhysicalStoreNameSemantics RequireSupportedNameProfile(
        PhysicalStoreNameSemantics actual,
        PhysicalRootIdentity? expectedRoot,
        PhysicalStoreNameSemantics? expectedSemantics = null)
    {
        var expectedEncoding = OperatingSystem.IsWindows()
            ? PhysicalStoreNameEncoding.Utf16LittleEndian
            : PhysicalStoreNameEncoding.Utf8;
        var supportedProfile = actual.ProfileId switch
        {
            "windows-ntfs-name-v1" => OperatingSystem.IsWindows() && !actual.NormalizationInsensitive,
            "darwin-apfs-v1" => OperatingSystem.IsMacOS(),
            "linux-ext4-sensitive-v1" => OperatingSystem.IsLinux() &&
                actual.CaseSensitive && !actual.NormalizationInsensitive,
            "linux-ext4-casefold-v1" => OperatingSystem.IsLinux() &&
                !actual.CaseSensitive && actual.NormalizationInsensitive,
            _ => false
        };
        if (actual.Encoding != expectedEncoding || !supportedProfile ||
            (expectedSemantics is not null && actual != expectedSemantics))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedFilesystem,
                "The package metadata directory has an unsupported or changing native name profile.", expectedRoot);
        return actual;
    }

    private static void RequireSameVolume(
        PhysicalFileIdentity identity,
        PhysicalFileIdentity expected,
        PhysicalRootIdentity? root,
        string message)
    {
        if (!string.Equals(identity.Provider, expected.Provider, StringComparison.Ordinal) ||
            !string.Equals(identity.VolumeOrDeviceId, expected.VolumeOrDeviceId, StringComparison.Ordinal))
            throw Refuse(message, root);
    }

    private static PackageStoreAdmissionException Refuse(string message, PhysicalRootIdentity? root = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, root);

    /// <summary>
    /// Renames a completed staging directory onto the final install path.
    /// Returns <see langword="false"/> when a concurrent writer already published a complete extraction
    /// there, in which case the staged copy is redundant and the caller discards it. A directory left
    /// behind by an interrupted extraction is replaced instead, because it carries no marker and is
    /// therefore not in use.
    /// </summary>
    private static bool TryPublish(string stagingDirectory, string installDirectory)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Move(stagingDirectory, installDirectory);
                return true;
            }
            catch (IOException) when (Directory.Exists(installDirectory))
            {
                if (IsInstalled(installDirectory))
                {
                    return false;
                }

                if (attempt > 0)
                {
                    throw;
                }

                try
                {
                    Directory.Delete(installDirectory, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                    // A concurrent writer removed it first; the retried rename is what matters.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a value to a single safe path segment. Besides characters the platform rejects, this
    /// also neutralizes directory separators and relative segments so that a hostile package
    /// identifier, feed name, or version cannot escape the install root.
    /// </summary>
    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var buffer = value
            .Select(ch => invalidCharacters.Contains(ch)
                || ch == Path.DirectorySeparatorChar
                || ch == Path.AltDirectorySeparatorChar
                    ? '_'
                    : ch)
            .ToArray();
        var sanitized = new string(buffer);

        return sanitized is "" or "." or ".." ? "_" : sanitized;
    }
}
