namespace Nuplane.Abstractions;

/// <summary>
/// Represents a desired package request specifying the package, version constraints, and resolution preferences.
/// </summary>
/// <param name="Id">The package identifier.</param>
/// <param name="VersionRange">The NuGet version range constraint (e.g., "[1.0.0, 2.0.0)").</param>
/// <param name="FeedName">
/// The optional preferred feed name for resolution. When it names a remote feed, local directory feeds
/// remain eligible so cached packages can satisfy the request; when it names a local directory feed,
/// resolution is restricted to that directory.
/// </param>
/// <param name="UpdatePolicy">The update policy governing version selection.</param>
/// <param name="SourceName">The name of the desired-state source that produced this request.</param>
public sealed record PackageRequest(
    string Id,
    string VersionRange,
    string? FeedName,
    PackageUpdatePolicy UpdatePolicy,
    string SourceName);