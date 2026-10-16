using System.Text.Json.Serialization;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Admin;

/// <summary>Describes an observed state slot without authorizing access to a filesystem path.</summary>
/// <param name="ParentIdentity">The native parent-directory identity.</param>
/// <param name="NameProfile">The verified native lookup profile.</param>
/// <param name="NameEncoding">The native name encoding: Utf8 or Utf16LittleEndian.</param>
/// <param name="CaseSensitive">Whether the native profile distinguishes case.</param>
/// <param name="NormalizationInsensitive">Whether the native profile ignores normalization differences.</param>
/// <param name="CanonicalBasename">The exact observed single-component filename.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreStateSlotObservation(
    PhysicalFileIdentity ParentIdentity, string NameProfile, string NameEncoding,
    bool CaseSensitive, bool NormalizationInsensitive, string CanonicalBasename);

/// <summary>Supplies descriptive recovery-selection evidence for independent core verification.</summary>
/// <param name="RecoveryPolicyId">The versioned policy selecting this graph.</param>
/// <param name="SourceRevision">The positive state revision bound by that policy.</param>
/// <param name="SelectedRootNodeIds">The graph root nodes selected by the saved requests.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoverySelectionEvidence(
    string RecoveryPolicyId, long SourceRevision, IReadOnlyList<Guid> SelectedRootNodeIds);

/// <summary>Describes one requested root and its selected node for independent verification.</summary>
/// <param name="Request">The saved package request.</param>
/// <param name="SelectedNodeId">The selected node identifier.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoveryRootSelection(PackageRequest Request, Guid SelectedNodeId);

/// <summary>Describes one installed graph node without granting package access.</summary>
/// <param name="NodeId">The graph node identifier.</param>
/// <param name="Install">The exact descriptive install identity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoveryNode(Guid NodeId, PackageInstallIdentity Install);

/// <summary>Describes one selected dependency edge for independent verification.</summary>
/// <param name="FromNodeId">The node declaring the dependency.</param>
/// <param name="ToNodeId">The selected dependency node.</param>
/// <param name="RequestedPackageId">The requested package identifier.</param>
/// <param name="RequestedVersionRange">The saved version range.</param>
/// <param name="TargetFramework">The dependency group framework, or empty for an agnostic group.</param>
/// <param name="IsOptional">Whether the dependency is optional.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoveryEdge(
    Guid FromNodeId, Guid ToNodeId, string RequestedPackageId,
    string RequestedVersionRange, string TargetFramework, bool IsOptional);

/// <summary>Supplies a complete graph candidate, without granting access to any described installation.</summary>
/// <remarks>Core validates graph semantics, exact native install identities and source state under quiescent ownership.</remarks>
/// <param name="SnapshotId">The nonempty snapshot identifier.</param>
/// <param name="GraphId">The graph identifier.</param>
/// <param name="GenerationId">The graph generation identifier.</param>
/// <param name="Disposition">The active and/or recoverable dispositions.</param>
/// <param name="Roots">The physical roots represented by the graph.</param>
/// <param name="RequestedRoots">The complete saved root selections.</param>
/// <param name="Nodes">The complete installed node identities.</param>
/// <param name="Edges">The complete selected dependency edges.</param>
/// <param name="RecoverySelection">The saved selection evidence for a recoverable graph.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoveryGraphEvidence(
    Guid SnapshotId,
    string GraphId,
    string GenerationId,
    ProtectedGraphDisposition Disposition,
    IReadOnlyList<PhysicalRootIdentity> Roots,
    IReadOnlyList<PackageStoreRecoveryRootSelection> RequestedRoots,
    IReadOnlyList<PackageStoreRecoveryNode> Nodes,
    IReadOnlyList<PackageStoreRecoveryEdge> Edges,
    PackageStoreRecoverySelectionEvidence? RecoverySelection);

/// <summary>Binds supplied graph evidence to one exact observed source state.</summary>
/// <param name="MemberId">The configured member identifier.</param>
/// <param name="ExpectedStateSlot">The observed canonical state slot.</param>
/// <param name="ExpectedStateFileIdentity">The observed state-file identity.</param>
/// <param name="ExpectedStateBodyDigest">The observed state-body digest.</param>
/// <param name="ActiveGraphs">The complete active graph candidates.</param>
/// <param name="RecoverableGraphs">The complete recoverable graph candidates.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreRecoveryEvidence(
    string MemberId,
    PackageStoreStateSlotObservation ExpectedStateSlot,
    PhysicalFileIdentity ExpectedStateFileIdentity,
    string ExpectedStateBodyDigest,
    IReadOnlyList<PackageStoreRecoveryGraphEvidence> ActiveGraphs,
    IReadOnlyList<PackageStoreRecoveryGraphEvidence> RecoverableGraphs);

/// <summary>Confirms a fully quiescent cutover using configured declarations, never caller-supplied state paths.</summary>
/// <remarks>Every store user must remain stopped until a Complete result is verified. Initial enrollment uses epoch 1 and an empty current roster.</remarks>
/// <param name="RootLabel">The configured root label.</param>
/// <param name="ExpectedPhysicalRootIdentity">The exact observed root identity.</param>
/// <param name="ExpectedEnrollmentEpoch">Initial epoch 1, or the exact current epoch for recovery or migration.</param>
/// <param name="QuiescentCutoverConfirmed">Explicit operator assertion that every store user is and remains stopped.</param>
/// <param name="IntendedMemberIds">The complete intended roster; configuration supplies its state locators.</param>
/// <param name="ExpectedCurrentMemberIds">The complete current roster, empty only for first enrollment.</param>
/// <param name="RetireMemberIds">Explicitly retired prior members; a smaller intended roster alone does not retire protection.</param>
/// <param name="RecoveryEvidence">Optional complete, source-bound evidence for unknown legacy closures.</param>
/// <param name="RetireLegacyRecoveryMemberIds">Explicitly retires unknown historical recovery selections for these members, with source-bound active evidence; active packages remain protected.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageStoreEnrollmentRequest(
    string RootLabel,
    PhysicalRootIdentity ExpectedPhysicalRootIdentity,
    long ExpectedEnrollmentEpoch,
    bool QuiescentCutoverConfirmed,
    IReadOnlyList<string> IntendedMemberIds,
    IReadOnlyList<string> ExpectedCurrentMemberIds,
    IReadOnlyList<string>? RetireMemberIds = null,
    IReadOnlyList<PackageStoreRecoveryEvidence>? RecoveryEvidence = null,
    IReadOnlyList<string>? RetireLegacyRecoveryMemberIds = null);

/// <summary>Describes one member's enrollment, recovery, migration or retirement.</summary>
/// <param name="MemberId">The configured member identifier.</param>
/// <param name="Disposition">The completed or refused disposition.</param>
/// <param name="Diagnostics">Reasons for the disposition.</param>
public sealed record PackageStoreEnrollmentMemberResult(
    string MemberId, string Disposition, IReadOnlyList<string> Diagnostics);

/// <summary>Reports observed enrollment state; store users may resume only after verified Complete publication.</summary>
/// <param name="RootLabel">The configured root label.</param>
/// <param name="RootIdentity">The observed physical root, if available.</param>
/// <param name="EnrollmentEpoch">The observed epoch, if established.</param>
/// <param name="Status">Complete, Incomplete or Refused.</param>
/// <param name="Members">Completed or refused member outcomes.</param>
/// <param name="Diagnostics">Operation diagnostics.</param>
public sealed record PackageStoreEnrollmentResult(
    string RootLabel,
    PhysicalRootIdentity? RootIdentity,
    long? EnrollmentEpoch,
    string Status,
    IReadOnlyList<PackageStoreEnrollmentMemberResult> Members,
    IReadOnlyList<PackageStoreMaintenanceDiagnostic> Diagnostics);
