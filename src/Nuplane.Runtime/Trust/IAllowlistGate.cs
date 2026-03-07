using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Trust;

/// <summary>
/// Enforces package allowlist rules and validates that package install paths
/// are within the trusted store root directory.
/// </summary>
public interface IAllowlistGate
{
    /// <summary>
    /// Filters package requests through the allowlist, throwing if any are rejected.
    /// </summary>
    /// <param name="requests">The package requests to validate.</param>
    /// <param name="trustOptions">The source trust options containing the allowlist.</param>
    /// <returns>The accepted package requests.</returns>
    /// <exception cref="AggregateException">Thrown when one or more packages are not allowlisted.</exception>
    IReadOnlyList<PackageRequest> Enforce(IReadOnlyList<PackageRequest> requests, SourceTrustOptions trustOptions);

    /// <summary>
    /// Validates that a package's active install path is within the trusted store root directory.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="activeInstallPath">The package's active install path.</param>
    /// <param name="rootDirectory">The trusted store root directory.</param>
    /// <exception cref="InvalidOperationException">Thrown when the path is outside the root directory.</exception>
    void EnsureActiveStorePath(string packageId, string activeInstallPath, string rootDirectory);
}

