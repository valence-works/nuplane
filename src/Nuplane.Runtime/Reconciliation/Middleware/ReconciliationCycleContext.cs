using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class ReconciliationCycleContext
{
    public required string CorrelationId { get; init; }
    public required DateTimeOffset CycleStartedAt { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    // Trigger metadata
    public ReconciliationTrigger? Trigger { get; set; }

    // Desired state
    public IReadOnlyList<PackageRequest> DesiredRequests { get; set; } = [];
    public IReadOnlyList<PackageRequest> AllowlistedRequests { get; set; } = [];

    // Read result
    public DesiredReadResult? ReadResult { get; set; }

    // Resolution
    public PackageResolutionResult? ResolutionResult { get; set; }
    public List<ResolvedPackage> TrustAndLockPassed { get; set; } = [];

    // Failure counts
    public int TrustFailureCount { get; set; }
    public int LockFailureCount { get; set; }
    public int CleanupFailureCount { get; set; }
    public int UnloadPendingCount { get; set; }
    public int SourceOutageCount { get; set; }

    // Diff and change
    public PackageChangeSet? ChangeSet { get; set; }
    public PackageApplyExecutionResult? ApplyResult { get; set; }

    // Active state
    public IReadOnlyDictionary<string, string>? ActiveVersions { get; set; }
    public Dictionary<string, string>? MergedActive { get; set; }

    // Result
    public ReconciliationRunResult? Result { get; set; }
}


