namespace Nuplane.Abstractions;

/// <summary>
/// Reason codes for reconciliation cycle outcomes and stage results.
/// </summary>
public static class ReconciliationReasonCodes
{
    /// <summary>Manifest was read and parsed successfully.</summary>
    public const string ManifestSucceeded = "manifest.succeeded";

    /// <summary>Manifest file was not found at the configured path.</summary>
    public const string ManifestNotFound = "manifest.not_found";

    /// <summary>Manifest file could not be read (I/O or access error).</summary>
    public const string ManifestUnreadable = "manifest.unreadable";

    /// <summary>Manifest content is invalid (schema, duplicate IDs, version ranges).</summary>
    public const string ManifestInvalid = "manifest.invalid";

    /// <summary>Rollback to last-known-good was performed.</summary>
    public const string RollbackPerformed = "rollback.performed";

    /// <summary>Rollback was not necessary (no mutation occurred).</summary>
    public const string RollbackNotRequired = "rollback.not_required";
}
