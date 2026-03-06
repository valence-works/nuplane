namespace Nuplane.Abstractions;

/// <summary>
/// Defines the scope of a convergence failure event.
/// </summary>
public enum FailureScope
{
    /// <summary>Failure in a desired-state source.</summary>
    Source,

    /// <summary>Failure in the desired manifest reader.</summary>
    Manifest,

    /// <summary>Failure in package acquisition.</summary>
    Acquisition,

    /// <summary>Failure in the loader boundary.</summary>
    Loader,

    /// <summary>Failure in the admin operational surface.</summary>
    Admin
}