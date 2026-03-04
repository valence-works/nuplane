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

/// <summary>
/// Status of a desired manifest read operation.
/// </summary>
public enum ManifestReadStatus
{
    /// <summary>Manifest was read and parsed successfully.</summary>
    Succeeded,

    /// <summary>Manifest file was not found.</summary>
    NotFound,

    /// <summary>Manifest file could not be read.</summary>
    Unreadable,

    /// <summary>Manifest content is invalid.</summary>
    Invalid
}

/// <summary>
/// Status of a reconciliation cycle outcome.
/// </summary>
public enum ReconciliationCycleStatus
{
    /// <summary>Cycle completed successfully with all packages converged.</summary>
    Succeeded,

    /// <summary>Cycle completed but with degraded outcomes for some packages or sources.</summary>
    Degraded,

    /// <summary>Cycle failed without mutating any state.</summary>
    FailedNonMutating
}

/// <summary>
/// Type of reconciliation cycle trigger.
/// </summary>
public enum ReconciliationTriggerType
{
    /// <summary>Triggered at application startup.</summary>
    Startup,

    /// <summary>Triggered by periodic polling.</summary>
    Polling,

    /// <summary>Triggered by an explicit manual request.</summary>
    Manual
}

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

/// <summary>
/// Outcome code for an admin trigger operation.
/// </summary>
public enum AdminTriggerOutcome
{
    /// <summary>Trigger was accepted and queued for execution.</summary>
    Accepted,

    /// <summary>Trigger was rejected (e.g., single-flight protection).</summary>
    Rejected,

    /// <summary>Admin surface is unavailable.</summary>
    Unavailable,

    /// <summary>Trigger completed execution.</summary>
    Completed
}

/// <summary>
/// Health state of the convergence system.
/// </summary>
public enum ConvergenceHealthState
{
    /// <summary>System is healthy with no outstanding failures.</summary>
    Healthy,

    /// <summary>System has degraded behavior due to failures.</summary>
    Degraded
}
