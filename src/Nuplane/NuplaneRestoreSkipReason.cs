namespace Nuplane;

/// <summary>
/// Why a host-free restore did nothing. Reported on
/// <see cref="NuplaneRestoreResult.SkipReason"/> whenever
/// <see cref="NuplaneRestoreResult.Skipped"/> is <see langword="true"/>.
/// </summary>
public enum NuplaneRestoreSkipReason
{
    /// <summary>
    /// The restore ran.
    /// </summary>
    None,

    /// <summary>
    /// Another process — or another Nuplane composition in this process — is reconciling the same
    /// store, so this restore declined rather than becoming a second writer. Nothing was resolved,
    /// acquired, or written. The caller may retry.
    /// </summary>
    StoreLockUnavailable,

    /// <summary>
    /// <see cref="NuplaneRestoreOptions.RequirePinnedVersions"/> was set and at least one desired
    /// request does not name a single version. Nothing was resolved, acquired, or written; the
    /// offending requests are in <see cref="NuplaneRestoreResult.UnpinnedRequests"/>.
    /// </summary>
    UnpinnedRequests
}
