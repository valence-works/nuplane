using Nuplane.Abstractions;

namespace Nuplane.Runtime.Loading;

/// <summary>
/// Outcome of a per-package loader boundary operation.
/// </summary>
public enum PackageLoaderOutcome
{
    /// <summary>The package was successfully loaded.</summary>
    Loaded,

    /// <summary>The package load failed — the failure is isolated.</summary>
    Failed,

    /// <summary>The package load was skipped (e.g. loading is disabled).</summary>
    Skipped
}

/// <summary>
/// Represents the result of a loader boundary operation for a single package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="Outcome">The loader outcome for this package.</param>
/// <param name="ReasonCode">The reason code explaining the outcome, if applicable.</param>
public sealed record PackageLoaderBoundaryEntry(
    string PackageId,
    string Version,
    PackageLoaderOutcome Outcome,
    string? ReasonCode);

/// <summary>
/// Represents the aggregate result of a loader boundary invocation across all packages.
/// </summary>
/// <param name="Entries">Per-package loader outcome entries.</param>
public sealed record PackageLoaderBoundaryResult(
    IReadOnlyList<PackageLoaderBoundaryEntry> Entries);

/// <summary>
/// Defines the runtime-level boundary for optional package loading. Implementations
/// delegate to a concrete loader when enabled, or emit deterministic <see cref="PackageLoaderOutcome.Skipped"/>
/// outcomes when loading is disabled.
/// </summary>
public interface IPackageLoaderBoundary
{
    /// <summary>
    /// Loads the specified packages via the configured loader, returning per-package outcomes.
    /// When loading is disabled, all entries are <see cref="PackageLoaderOutcome.Skipped"/>.
    /// Loader failures MUST be isolated per-package and MUST NOT crash the host.
    /// </summary>
    /// <param name="packages">The resolved packages to load.</param>
    /// <param name="correlationId">The correlation identifier for the current reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Per-package loader outcomes.</returns>
    Task<PackageLoaderBoundaryResult> LoadAsync(
        IReadOnlyList<ResolvedPackage> packages,
        string correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A no-op loader boundary that emits <see cref="PackageLoaderOutcome.Skipped"/> for all packages.
/// Used when the optional loader SDK is not registered.
/// </summary>
internal sealed class NoOpPackageLoaderBoundary : IPackageLoaderBoundary
{
    /// <inheritdoc />
    public Task<PackageLoaderBoundaryResult> LoadAsync(
        IReadOnlyList<ResolvedPackage> packages,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var entries = packages.Select(p => new PackageLoaderBoundaryEntry(
            p.Id, p.Version, PackageLoaderOutcome.Skipped, "loader-disabled")).ToList();
        return Task.FromResult(new PackageLoaderBoundaryResult(entries));
    }
}
