using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class ReconciliationCycleContext
{
    public required string CorrelationId { get; init; }
    public required DateTimeOffset CycleStartedAt { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    // Trigger metadata
    public ReconciliationTrigger? Trigger { get; set; }

    /// <summary>Present only while this cycle retains its enrolled package-store owner.</summary>
    public PackageStoreOperationOwner? PackageStoreOwner { get; set; }

    /// <summary>Whether complete graph/history preflight passed before enrolled transaction execution.</summary>
    public bool CoordinatedTransitionPreflightPassed { get; set; }

    /// <summary>Whether final health/metrics construction is deferred until post-admission loading finishes.</summary>
    public bool DeferCycleCompletion { get; set; }

    // Desired state
    public IReadOnlyList<PackageRequest> DesiredRequests { get; set; } = [];

    // Read result
    public DesiredReadResult? ReadResult { get; set; }

    // Resolution
    public PackageResolutionResult? ResolutionResult { get; set; }
    public LockFileSnapshot? LockFileSnapshot { get; set; }
    public List<ResolvedPackage> TrustAndLockPassed { get; set; } = [];

    // Failure counts
    public int LockFailureCount { get; set; }
    public int CleanupFailureCount { get; set; }
    public int SourceOutageCount { get; set; }

    // Diff and change
    public PackageChangeSet? ChangeSet { get; set; }
    public PackageApplyExecutionResult? ApplyResult { get; set; }

    // Active state
    public IReadOnlyDictionary<string, string>? ActiveVersions { get; set; }
    public Dictionary<string, string>? MergedActive { get; set; }

    // Result
    public ReconciliationRunResult? Result { get; set; }

    /// <summary>Deferred package-load failures returned after the enrolled cycle releases short locks.</summary>
    public IReadOnlyList<string> DeferredLoadingFailedPackageIds { get; set; } = [];
}
