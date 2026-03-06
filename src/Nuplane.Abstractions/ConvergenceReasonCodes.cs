namespace Nuplane.Abstractions;

/// <summary>
/// Reason codes for convergence cycle outcomes and stage results.
/// </summary>
public static class ConvergenceReasonCodes
{
    /// <summary>Manifest was read and parsed successfully.</summary>
    public const string ManifestSucceeded = "manifest.succeeded";

    /// <summary>Manifest file was not found at the configured path.</summary>
    public const string ManifestNotFound = "manifest.not_found";

    /// <summary>Manifest file could not be read (I/O or access error).</summary>
    public const string ManifestUnreadable = "manifest.unreadable";

    /// <summary>Manifest content is invalid (schema, duplicate IDs, version ranges).</summary>
    public const string ManifestInvalid = "manifest.invalid";

    /// <summary>Source was evaluated successfully.</summary>
    public const string SourceSucceeded = "source.succeeded";

    /// <summary>Source is unavailable (timeout, network, or transient failure).</summary>
    public const string SourceUnavailable = "source.unavailable";

    /// <summary>Duplicate package ID was resolved via deterministic tie-break.</summary>
    public const string DuplicateResolved = "aggregation.duplicate_resolved";

    /// <summary>Acquisition stage completed successfully.</summary>
    public const string AcquisitionSucceeded = "acquisition.succeeded";

    /// <summary>Acquisition stage failed for a package.</summary>
    public const string AcquisitionFailed = "acquisition.failed";

    /// <summary>Loader boundary loaded a package successfully.</summary>
    public const string LoaderLoaded = "loader.loaded";

    /// <summary>Loader boundary failed to load a package.</summary>
    public const string LoaderFailed = "loader.failed";

    /// <summary>Loader was disabled; package loading was skipped.</summary>
    public const string LoaderSkipped = "loader.skipped";

    /// <summary>Admin trigger was accepted.</summary>
    public const string AdminAccepted = "admin.accepted";

    /// <summary>Admin trigger was rejected (e.g., single-flight active).</summary>
    public const string AdminRejected = "admin.rejected";

    /// <summary>Admin surface is unavailable (disabled or not wired).</summary>
    public const string AdminUnavailable = "admin.unavailable";

    /// <summary>Admin trigger completed successfully.</summary>
    public const string AdminCompleted = "admin.completed";

    /// <summary>Rollback to last-known-good was performed.</summary>
    public const string RollbackPerformed = "rollback.performed";

    /// <summary>Rollback was not necessary (no mutation occurred).</summary>
    public const string RollbackNotRequired = "rollback.not_required";
}