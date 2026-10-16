using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Admin;

/// <summary>Describes observed state revisions without granting access or deletion authority.</summary>
/// <param name="MemberId">The enrolled member identifier.</param>
/// <param name="Revision">The observed protection revision.</param>
/// <param name="StateBodyDigest">The verified state-body digest.</param>
/// <param name="ProtectionDigest">The verified protection digest.</param>
public sealed record PackageStoreMemberObservation(
    string MemberId, long Revision, string StateBodyDigest, string ProtectionDigest);

/// <summary>Describes an inventory entry, including preserved unknown and staging entries.</summary>
/// <param name="RelativePath">The descriptive root-relative path, never an execution input.</param>
/// <param name="Kind">The inventory classification.</param>
/// <param name="Reason">Any uncertainty reason.</param>
/// <param name="Install">The exact completed-install identity when recognized.</param>
public sealed record PackageStoreInventoryObservation(
    string RelativePath, string Kind, string? Reason, PackageInstallIdentity? Install);

/// <summary>Describes uncertainty that prevents a complete inventory.</summary>
/// <param name="RelativePath">The affected root-relative path, or empty for a root-wide issue.</param>
/// <param name="Reason">The diagnostic reason.</param>
public sealed record PackageStoreInventoryUncertainty(string RelativePath, string Reason);

/// <summary>Describes a protected graph-use record and its nonblocking ownership observation.</summary>
/// <param name="UseId">The graph-use record identifier.</param>
/// <param name="OwnershipState">The observed live or stale sentinel state.</param>
/// <param name="SnapshotState">The observed graph snapshot state.</param>
/// <param name="Graph">The detached protected graph.</param>
public sealed record PackageStoreUseObservation(
    Guid UseId, string OwnershipState, string SnapshotState, ProtectedGraphSnapshot Graph);

/// <summary>Reports policy classification separately from actual physical work.</summary>
/// <param name="Install">The exact completed-install identity.</param>
/// <param name="Classification">Retained, Eligible or Refused by the fresh policy plan.</param>
/// <param name="Reasons">The retention and protection reasons.</param>
/// <param name="Attempt">NotAttempted, Completed, Refused, Failed or Cancelled.</param>
/// <param name="PhysicalOutcome">Observed physical facts, or null when no physical outcome was established.</param>
/// <param name="JournalPhase">The last observed durable deletion phase.</param>
/// <param name="MissingAtStart">Entries positively absent when this invocation began, when observed.</param>
/// <param name="MissingAtLastObservation">Entries positively absent at the last observation, when observed.</param>
/// <param name="Diagnostic">The candidate diagnostic, if work stopped.</param>
public sealed record PackageStoreCandidateResult(
    PackageInstallIdentity Install,
    string Classification,
    IReadOnlyList<string> Reasons,
    string Attempt,
    string? PhysicalOutcome,
    string? JournalPhase,
    IReadOnlyList<string>? MissingAtStart,
    IReadOnlyList<string>? MissingAtLastObservation,
    PackageStoreMaintenanceDiagnostic? Diagnostic);

/// <summary>Provides a typed admission refusal or an execution failure diagnostic.</summary>
/// <param name="Code">The admission reason or failure category.</param>
/// <param name="Message">The diagnostic explanation.</param>
public sealed record PackageStoreMaintenanceDiagnostic(string Code, string Message);

/// <summary>Returns a detached inspection or execution report; previews never authorize later deletion.</summary>
/// <remarks>Completed physical facts remain present if later work fails or is cancelled.</remarks>
/// <param name="RootLabel">The configured label used by this operation.</param>
/// <param name="Mode">The requested maintenance mode.</param>
/// <param name="Status">Preview, Completed, Refused, Failed or Cancelled.</param>
/// <param name="RootIdentity">The observed native root identity.</param>
/// <param name="EnrollmentEpoch">The observed enrollment epoch, when enrolled.</param>
/// <param name="LedgerDigest">The observed membership ledger digest.</param>
/// <param name="ProtectionKnowledge">Known or Unknown protection.</param>
/// <param name="InventoryComplete">Whether the bounded inventory completed without uncertainty.</param>
/// <param name="Retention">The policy used by the fresh plan.</param>
/// <param name="Members">Observed member revisions and digests.</param>
/// <param name="Inventory">Observed package, unknown and staging entries.</param>
/// <param name="InventoryIssues">Inventory uncertainty.</param>
/// <param name="Uses">Observed live and stale graph-use records.</param>
/// <param name="Candidates">Policy classifications and, for execution, actual physical outcomes.</param>
/// <param name="Diagnostics">Conservative planning or execution diagnostics.</param>
/// <param name="CorrelationId">Diagnostic correlation only.</param>
public sealed record PackageStoreMaintenanceResult(
    string RootLabel,
    PackageStorePruneMode Mode,
    string Status,
    PhysicalRootIdentity? RootIdentity,
    long? EnrollmentEpoch,
    string? LedgerDigest,
    string ProtectionKnowledge,
    bool InventoryComplete,
    PackageStoreRetentionPolicy? Retention,
    IReadOnlyList<PackageStoreMemberObservation> Members,
    IReadOnlyList<PackageStoreInventoryObservation> Inventory,
    IReadOnlyList<PackageStoreInventoryUncertainty> InventoryIssues,
    IReadOnlyList<PackageStoreUseObservation> Uses,
    IReadOnlyList<PackageStoreCandidateResult> Candidates,
    IReadOnlyList<PackageStoreMaintenanceDiagnostic> Diagnostics,
    string? CorrelationId);
