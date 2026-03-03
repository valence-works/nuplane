namespace Nuplane.Abstractions;

/// <summary>
/// Resolves a package request to a concrete installed package.
/// </summary>
public interface IPackageResolver
{
    /// <summary>
    /// Resolves the specified package request to a concrete package version.
    /// </summary>
    /// <param name="request">The package request to resolve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved package with concrete version and install path.</returns>
    Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken);
}

