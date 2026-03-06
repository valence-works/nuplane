namespace Nuplane.Loading;

/// <summary>
/// Contains the results of a batch package load operation, including successfully loaded sessions
/// and a dictionary of failures keyed by package identifier.
/// </summary>
/// <param name="Loaded">The list of packages that were successfully loaded.</param>
/// <param name="FailedByPackageId">A dictionary mapping failed package identifiers to their error messages.</param>
public sealed record PackageLoadResult(
    IReadOnlyList<PackageLoadSession> Loaded,
    IReadOnlyDictionary<string, string> FailedByPackageId);