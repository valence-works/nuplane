namespace Nuplane.Loading;

/// <summary>
/// Represents the state of a loaded package assembly, including its load context key and
/// any error that occurred during loading.
/// </summary>
/// <param name="PackageId">The identifier of the loaded package.</param>
/// <param name="Version">The version of the loaded package.</param>
/// <param name="ActiveInstallPath">The file system path where the package is installed.</param>
/// <param name="ContextKey">The unique key identifying the assembly load context for this package.</param>
/// <param name="LoadedAt">The time at which the package was loaded.</param>
/// <param name="IsLoaded">Whether the package was successfully loaded into an assembly context.</param>
/// <param name="LastError">The error message from the last failed load attempt, if any.</param>
public sealed record PackageLoadSession(
    string PackageId,
    string Version,
    string ActiveInstallPath,
    string ContextKey,
    DateTimeOffset LoadedAt,
    bool IsLoaded,
    string? LastError);