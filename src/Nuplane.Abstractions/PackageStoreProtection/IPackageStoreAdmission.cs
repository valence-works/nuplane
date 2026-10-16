namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Acquires operation ownership before accessing package-store paths.</summary>
/// <remarks>
/// Implementations return an unenrolled result only when ordinary behavior is safe. Unknown or
/// incomplete authority is a typed refusal and must occur before package-content I/O.
/// </remarks>
public interface IPackageStoreAdmission
{
    /// <summary>Acquires admission for a configured package-store root.</summary>
    /// <param name="kind">The operation being performed.</param>
    /// <param name="cancellationToken">Cancels admission before ownership is returned.</param>
    /// <returns>An immutable admission result whose owner must be disposed by the caller.</returns>
    ValueTask<PackageStoreRootOperationAdmission> AcquireConfiguredRootOperationAsync(
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>Classifies all paths and acquires their enrolled roots in stable physical order.</summary>
    /// <param name="installPaths">The complete set of paths the operation may access.</param>
    /// <param name="kind">The operation being performed.</param>
    /// <param name="cancellationToken">Cancels admission before ownership is returned.</param>
    /// <returns>An immutable path admission. Enrolled paths are accessed through scoped borrows.</returns>
    /// <exception cref="PackageStoreAdmissionException">Authority is unknown, incomplete, or unsupported.</exception>
    ValueTask<PackageStorePathAdmission> AcquireForInstallPathsAsync(
        IReadOnlyCollection<string> installPaths,
        PackageStoreAdmissionKind kind,
        CancellationToken cancellationToken = default);
}
