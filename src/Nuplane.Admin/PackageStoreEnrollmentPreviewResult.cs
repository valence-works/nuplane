using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Admin;

/// <summary>Describes the knowledge of a member's protected graph closure.</summary>
/// <param name="Knowledge">Known or Unknown.</param>
/// <param name="UnknownReason">The reason when the closure is unknown.</param>
/// <param name="GraphIds">Observed graph identities when known; an empty list is KnownEmpty.</param>
public sealed record PackageStoreEnrollmentClosureObservation(
    string Knowledge, string? UnknownReason, IReadOnlyList<string>? GraphIds);

/// <summary>Describes one configured member's enrollment or migration requirements.</summary>
/// <param name="MemberId">The configured member identifier.</param>
/// <param name="BindingKind">The observed membership binding.</param>
/// <param name="Observation">The state observation status.</param>
/// <param name="StateBodyDigest">The observed state-body digest, when available.</param>
/// <param name="StateSlot">The observed canonical state slot, when available; descriptive only.</param>
/// <param name="StateFileIdentity">The observed state-file identity, when available.</param>
/// <param name="ActiveVersions">Observed active versions, without granting package access.</param>
/// <param name="LastKnownGoodVersions">Observed last-known-good map; this alone is not a verified closure.</param>
/// <param name="ActiveClosure">Active graph knowledge.</param>
/// <param name="RecoverableClosure">Recoverable graph knowledge.</param>
/// <param name="Migration">Required enrollment or recovery work.</param>
/// <param name="Diagnostics">Reasons requiring operator attention.</param>
public sealed record PackageStoreEnrollmentMemberObservation(
    string MemberId,
    string BindingKind,
    string Observation,
    string? StateBodyDigest,
    PackageStoreStateSlotObservation? StateSlot,
    PhysicalFileIdentity? StateFileIdentity,
    IReadOnlyDictionary<string, string> ActiveVersions,
    IReadOnlyDictionary<string, string> LastKnownGoodVersions,
    PackageStoreEnrollmentClosureObservation ActiveClosure,
    PackageStoreEnrollmentClosureObservation RecoverableClosure,
    string Migration,
    IReadOnlyList<string> Diagnostics);

/// <summary>Returns non-quiescent descriptive observations; this report cannot authorize enrollment or pruning.</summary>
/// <param name="RootLabel">The configured root label.</param>
/// <param name="RootIdentity">The observed native root identity, if available.</param>
/// <param name="Membership">Unenrolled, Incomplete or Complete, when known.</param>
/// <param name="EnrollmentEpoch">The observed epoch, if enrolled; initial enrollment explicitly uses epoch 1.</param>
/// <param name="LedgerDigest">The observed ledger digest.</param>
/// <param name="IsNonQuiescentObservation">True because observation does not prove that all users are stopped.</param>
/// <param name="Members">The complete configured member observations.</param>
/// <param name="Diagnostics">Root or declaration diagnostics.</param>
/// <param name="CurrentMemberIds">The exact observed ledger roster, empty when unenrolled, or null when unavailable.</param>
public sealed record PackageStoreEnrollmentPreviewResult(
    string RootLabel,
    PhysicalRootIdentity? RootIdentity,
    string? Membership,
    long? EnrollmentEpoch,
    string? LedgerDigest,
    bool IsNonQuiescentObservation,
    IReadOnlyList<PackageStoreEnrollmentMemberObservation> Members,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string>? CurrentMemberIds = null);
