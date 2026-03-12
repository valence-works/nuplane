namespace Nuplane.Store.Cleanup;

/// <summary>
/// Specifies when cleanup operations are executed.
/// </summary>
public enum CleanupExecutionMode
{
    /// <summary>Cleanup runs automatically after each successful reconciliation cycle.</summary>
    Automatic,
    /// <summary>Cleanup is only triggered manually.</summary>
    ManualOnly
}