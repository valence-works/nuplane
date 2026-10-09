namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Identifies a selected dependency edge between two graph nodes.</summary>
public sealed record PackageGraphEdgeIdentity
{
    /// <summary>Initializes a dependency-edge identity for use by Nuplane core.</summary>
    /// <param name="fromNodeId">The node declaring the dependency.</param>
    /// <param name="toNodeId">The selected dependency node.</param>
    /// <param name="requestedPackageId">The requested dependency package identifier.</param>
    /// <param name="requestedVersionRange">The dependency version range.</param>
    /// <param name="targetFramework">The dependency group target framework.</param>
    /// <param name="isOptional">Whether the dependency is optional.</param>
    /// <exception cref="ArgumentException">A node identifier is empty or a required text value is blank.</exception>
    internal PackageGraphEdgeIdentity(
        Guid fromNodeId,
        Guid toNodeId,
        string requestedPackageId,
        string requestedVersionRange,
        string targetFramework,
        bool isOptional)
    {
        if (fromNodeId == Guid.Empty)
            throw new ArgumentException("The declaring node identifier cannot be empty.", nameof(fromNodeId));
        if (toNodeId == Guid.Empty)
            throw new ArgumentException("The dependency node identifier cannot be empty.", nameof(toNodeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedVersionRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        RequestedPackageId = requestedPackageId;
        RequestedVersionRange = requestedVersionRange;
        TargetFramework = targetFramework;
        IsOptional = isOptional;
    }

    /// <summary>Gets the declaring node identifier.</summary>
    public Guid FromNodeId { get; }

    /// <summary>Gets the selected dependency node identifier.</summary>
    public Guid ToNodeId { get; }

    /// <summary>Gets the requested dependency package identifier.</summary>
    public string RequestedPackageId { get; }

    /// <summary>Gets the requested dependency version range.</summary>
    public string RequestedVersionRange { get; }

    /// <summary>Gets the target framework group that contributed the dependency.</summary>
    public string TargetFramework { get; }

    /// <summary>Gets whether the dependency is optional.</summary>
    public bool IsOptional { get; }
}
