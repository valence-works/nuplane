namespace Nuplane.Metadata;

/// <summary>
/// Reads and validates a resolved package's package-root <c>nuplane.json</c>. Implemented by
/// <see cref="NuplanePackageMetadataReader"/>, which is the only implementation Nuplane has; the
/// interface exists because reconciliation depends on this read, and a dependency a cycle performs
/// per resolved package is one a test has to be able to count.
/// </summary>
internal interface IPackageMetadataReader
{
    /// <summary>
    /// Reads the <c>nuplane.json</c> document at the root of a resolved package's install path.
    /// </summary>
    /// <param name="packageId">The declaring package's id.</param>
    /// <param name="version">The declaring package's version.</param>
    /// <param name="installPath">The package's install root directory.</param>
    /// <returns>Whether metadata was found and, if so, whether it is valid.</returns>
    NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath);
}
