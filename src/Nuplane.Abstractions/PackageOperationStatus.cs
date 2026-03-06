namespace Nuplane.Abstractions;

/// <summary>
/// Status of a per-package acquisition or loader operation.
/// </summary>
public enum PackageOperationStatus
{
    /// <summary>Operation completed successfully.</summary>
    Succeeded,

    /// <summary>Operation failed.</summary>
    Failed,

    /// <summary>Operation was skipped.</summary>
    Skipped
}