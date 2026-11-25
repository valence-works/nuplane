using Nuplane.Abstractions;

namespace Nuplane;

/// <summary>
/// Reads an active package's files from the exact <c>.nupkg</c> Nuplane acquired for it, after checking the
/// archive against the sha512 recorded at acquisition.
/// </summary>
/// <remarks>
/// This reader needs no package-store enrollment and no filesystem qualification: it trusts the archive
/// bytes only after they match <see cref="ActivePackage.ArchiveHash"/>, so it works on any filesystem
/// that can store and read a file. A missing, modified or replaced archive is refused with a
/// <see cref="PackageArchiveException"/> that names the package and the reason.
/// </remarks>
public interface IPackageArchiveReader
{
    /// <summary>Opens the verified archive of a package from an active-package snapshot.</summary>
    /// <param name="package">The package, as an <see cref="IActivePackageCatalog"/> snapshot reports it.</param>
    /// <param name="cancellationToken">
    /// Cancels this caller's wait. A read already in progress finishes for other callers of the same archive.
    /// </param>
    /// <returns>The verified archive. It holds the archive bytes in memory and needs no disposal.</returns>
    /// <remarks>A refusal is remembered for a few seconds, so repeated opens of a refused archive fail without rereading it.</remarks>
    /// <exception cref="PackageArchiveException">The archive is missing, unverifiable or does not match its recorded hash.</exception>
    ValueTask<PackageArchive> OpenAsync(ActivePackage package, CancellationToken cancellationToken = default);
}
