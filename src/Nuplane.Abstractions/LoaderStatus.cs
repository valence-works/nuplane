namespace Nuplane.Abstractions;

/// <summary>
/// Status of a loader boundary operation for an activated package.
/// </summary>
public enum LoaderStatus
{
    /// <summary>Package was loaded successfully.</summary>
    Loaded,

    /// <summary>Package loading failed.</summary>
    Failed,

    /// <summary>Package loading was skipped (loader disabled).</summary>
    Skipped
}