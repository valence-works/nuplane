using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using NuGet.Versioning;

namespace Nuplane.Feeds;

/// <summary>A local-feed archive selected by exact native parent, name, and file identity.</summary>
internal readonly record struct NativeLocalDirectoryPackageFile(
    string FileName,
    string PackageId,
    NuGetVersion Version,
    PhysicalFileIdentity ParentIdentity,
    PhysicalStoreNameSemantics Profile,
    PhysicalFileIdentity FileIdentity,
    long Length);

/// <summary>Indexes borrowed local feeds through native owner-aware directory access.</summary>
internal static class NativeLocalDirectoryFeedIndex
{
    internal static Task<IReadOnlyList<NativeLocalDirectoryPackageFile>> FindPackageFilesAsync(
        string directoryPath,
        string packageId,
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(borrow);
        cancellationToken.ThrowIfCancellationRequested();

        return DesiredSourceDirectoryAccess.WithDirectoryAsync(
            directoryPath,
            borrow,
            session => Task.FromResult(FindPackageFiles(session, packageId, cancellationToken)),
            cancellationToken);
    }

    internal static Task RevalidateSelectedPackageFileAsync(
        string directoryPath,
        NativeLocalDirectoryPackageFile selected,
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(borrow);
        cancellationToken.ThrowIfCancellationRequested();

        return DesiredSourceDirectoryAccess.WithDirectoryAsync(
            directoryPath,
            borrow,
            session =>
            {
                session.RevalidatePackageCandidate(selected.FileName, selected.ParentIdentity,
                    selected.Profile, selected.FileIdentity, selected.Length);
                return Task.FromResult(true);
            },
            cancellationToken);
    }

    internal static Task CopySelectedPackageArchiveAsync(
        string directoryPath,
        NativeLocalDirectoryPackageFile selected,
        PackageStoreOperationBorrow borrow,
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle heldRoot,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(borrow);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(heldRoot);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        return DesiredSourceDirectoryAccess.WithDirectoryInValidatedRootAsync(
            directoryPath,
            borrow,
            files,
            heldRoot,
            async session =>
            {
                await session.CopyPackageArchiveToAsync(
                    selected.FileName,
                    selected.ParentIdentity,
                    selected.Profile,
                    selected.FileIdentity,
                    selected.Length,
                    destination,
                    NativePackageInstallSession.MaximumArchiveBytes,
                    cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    internal static NativeLocalDirectoryPackageFile? SelectVersion(
        IReadOnlyList<NativeLocalDirectoryPackageFile> candidates,
        string version)
    {
        if (!NuGetVersion.TryParse(version, out var requestedVersion))
            return null;

        foreach (var candidate in candidates)
        {
            if (candidate.Version.Equals(requestedVersion))
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<NativeLocalDirectoryPackageFile> FindPackageFiles(
        DesiredSourceDirectorySession session,
        string packageId,
        CancellationToken cancellationToken)
    {
        var candidates = new List<NativeLocalDirectoryPackageFile>();
        foreach (var fileName in session.EnumeratePackageCandidates(StringComparison.OrdinalIgnoreCase, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LocalDirectoryFeedIndex.TryParsePackageFileName(fileName, packageId,
                    out var parsedPackageId, out var version))
            {
                continue;
            }

            var sample = session.SamplePackageCandidate(fileName);
            if (sample.Kind != PhysicalStoreDirectoryCandidateKind.RegularFile || sample.Identity is null)
            {
                throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnknownAuthority,
                    "A matching local package candidate is not a positively verified regular single-link file.");
            }

            var profile = session.Profile
                ?? throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnknownAuthority,
                    "A present local feed directory has no native name profile.");
            candidates.Add(new(fileName, parsedPackageId, version, session.DirectoryIdentity,
                profile, sample.Identity, sample.Length));
        }

        return candidates
            .OrderBy(static candidate => candidate.Version)
            .ThenBy(static candidate => candidate.FileName, StringComparer.Ordinal)
            .ToArray();
    }
}
