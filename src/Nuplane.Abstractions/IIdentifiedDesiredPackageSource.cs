namespace Nuplane.Abstractions;

/// <summary>Provides an instance identity for desired-state snapshots and source diagnostics.</summary>
public interface IIdentifiedDesiredPackageSource : IDesiredPackageSource
{
    /// <summary>
    /// Gets a nonblank identifier unique within the host and stable across restarts.
    /// Identifiers are compared case-insensitively. Package request source priorities continue to
    /// use <see cref="PackageRequest.SourceName"/>.
    /// </summary>
    string SourceInstanceId { get; }
}
