namespace Nuplane.Abstractions;

/// <summary>
/// Status of a per-package acquisition stage.
/// </summary>
public enum AcquisitionStage
{
    /// <summary>Package version resolution stage.</summary>
    Resolve,

    /// <summary>Package download stage.</summary>
    Download,

    /// <summary>Package validation stage.</summary>
    Validate,

    /// <summary>Package activation stage.</summary>
    Activate
}