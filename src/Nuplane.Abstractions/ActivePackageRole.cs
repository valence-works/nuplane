namespace Nuplane.Abstractions;

/// <summary>
/// Describes how an active package participates in its resolved graph.
/// </summary>
public enum ActivePackageRole
{
    /// <summary>
    /// The active package is an explicit desired root.
    /// </summary>
    Root,

    /// <summary>
    /// The active package is present only because another package depends on it.
    /// </summary>
    Dependency,

    /// <summary>
    /// The active package is both explicitly desired and selected as a dependency.
    /// </summary>
    RootAndDependency
}
