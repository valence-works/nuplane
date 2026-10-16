using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Maintenance;

internal enum PackageStoreEnrollmentPreviewMembership
{
    Unenrolled,
    Incomplete,
    Complete
}

internal enum PackageStoreEnrollmentPreviewObservation
{
    Observed,
    Missing,
    NullPayload,
    Malformed,
    Changed,
    Unavailable
}

internal enum PackageStoreEnrollmentPreviewMigration
{
    None,
    EnrollmentRequired,
    BindingRequired,
    ProtectionAcknowledgementRequired,
    RecoveryRequired,
    DeclarationMismatch,
    DeclarationUnavailable
}

internal sealed record PackageStoreEnrollmentPreviewClosure
{
    internal PackageStoreEnrollmentPreviewClosure(
        PackageProtectionClosureKnowledge knowledge,
        string? unknownReason,
        IEnumerable<string>? graphIds)
    {
        if (!Enum.IsDefined(knowledge))
            throw new ArgumentOutOfRangeException(nameof(knowledge));
        var ids = graphIds?.ToArray();
        if (knowledge == PackageProtectionClosureKnowledge.Unknown && (unknownReason is null || ids is not null) ||
            knowledge == PackageProtectionClosureKnowledge.Known && (unknownReason is not null || ids is null))
            throw new ArgumentException("The closure tag must match its reason and graph collection.");
        Knowledge = knowledge;
        UnknownReason = unknownReason;
        GraphIds = ids is null ? null : Array.AsReadOnly(ids);
    }

    internal PackageProtectionClosureKnowledge Knowledge { get; }
    internal string? UnknownReason { get; }
    internal IReadOnlyList<string>? GraphIds { get; }
}

internal sealed class PackageStoreEnrollmentPreviewMember
{
    internal PackageStoreEnrollmentPreviewMember(
        string memberId,
        string stateFileLocator,
        string bindingKind,
        PackageStoreEnrollmentPreviewObservation observation,
        DateTimeOffset observedAtUtc,
        StateSlotIdentity? stateSlot,
        PhysicalFileIdentity? stateFileIdentity,
        string? rawPayloadDigest,
        string? stateBodyDigest,
        StoreStateRecord? state,
        PackageStoreEnrollmentPreviewClosure activeClosure,
        PackageStoreEnrollmentPreviewClosure recoverableClosure,
        PackageStoreEnrollmentPreviewMigration migration,
        IEnumerable<string>? diagnostics = null,
        IEnumerable<RetiredGraphEvidence>? retiredGraphs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFileLocator);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKind);
        ArgumentNullException.ThrowIfNull(activeClosure);
        ArgumentNullException.ThrowIfNull(recoverableClosure);
        MemberId = memberId;
        StateFileLocator = stateFileLocator;
        BindingKind = bindingKind;
        Observation = observation;
        ObservedAtUtc = observedAtUtc;
        StateSlot = stateSlot is null ? null : new StateSlotIdentity(
            new PhysicalFileIdentity(stateSlot.ParentIdentity.Provider, stateSlot.ParentIdentity.VolumeOrDeviceId,
                stateSlot.ParentIdentity.FileId),
            new PhysicalStoreNameSemantics(stateSlot.NameSemantics.ProfileId, stateSlot.NameSemantics.Encoding,
                stateSlot.NameSemantics.CaseSensitive, stateSlot.NameSemantics.NormalizationInsensitive),
            stateSlot.CanonicalBasename);
        StateFileIdentity = CopyIdentity(stateFileIdentity);
        RawPayloadDigest = rawPayloadDigest;
        StateBodyDigest = stateBodyDigest;
        ActiveVersionById = CopyMap(state?.ActiveVersionById);
        LastKnownGoodById = CopyMap(state?.LastKnownGoodById);
        ActivePackageDescriptorsById = CopyDescriptors(state?.ActivePackageDescriptorsByIdNormalized);
        ActivationHistoryById = CopyActivationHistory(state?.ActiveGraphsByIdNormalized);
        StateUpdatedAtUtc = state?.UpdatedAt;
        ActiveClosure = activeClosure;
        RecoverableClosure = recoverableClosure;
        Migration = migration;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        RetiredGraphs = Array.AsReadOnly((retiredGraphs ?? []).Select(static graph => graph.Copy()).ToArray());
    }

    internal string MemberId { get; }
    internal string StateFileLocator { get; }
    internal string BindingKind { get; }
    internal PackageStoreEnrollmentPreviewObservation Observation { get; }
    internal DateTimeOffset ObservedAtUtc { get; }
    internal StateSlotIdentity? StateSlot { get; }
    internal PhysicalFileIdentity? StateFileIdentity { get; }
    internal string? RawPayloadDigest { get; }
    internal string? StateBodyDigest { get; }
    internal IReadOnlyDictionary<string, string> ActiveVersionById { get; }
    internal IReadOnlyDictionary<string, string> LastKnownGoodById { get; }
    internal IReadOnlyDictionary<string, ActivePackageDescriptor> ActivePackageDescriptorsById { get; }
    internal IReadOnlyDictionary<string, GraphActivationRecord> ActivationHistoryById { get; }
    internal DateTimeOffset? StateUpdatedAtUtc { get; }
    internal PackageStoreEnrollmentPreviewClosure ActiveClosure { get; }
    internal PackageStoreEnrollmentPreviewClosure RecoverableClosure { get; }
    internal PackageStoreEnrollmentPreviewMigration Migration { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
    /// <summary>
    /// Gets raw retired-graph declarations decoded from the observed state payload. They are descriptive history only;
    /// observation, closure, and binding status determine trust, and these declarations establish no retirement authority.
    /// </summary>
    internal IReadOnlyList<RetiredGraphEvidence> RetiredGraphs { get; }

    private static PhysicalFileIdentity? CopyIdentity(PhysicalFileIdentity? identity)
        => identity is null ? null : new(identity.Provider, identity.VolumeOrDeviceId, identity.FileId);

    private static IReadOnlyDictionary<string, string> CopyMap(Dictionary<string, string>? map)
        => new ReadOnlyDictionary<string, string>(map is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, ActivePackageDescriptor> CopyDescriptors(
        Dictionary<string, ActivePackageDescriptor>? descriptors)
    {
        var copied = (descriptors ?? []).ToDictionary(static pair => pair.Key, static pair => pair.Value with
        {
            RootPackageIds = Array.AsReadOnly(pair.Value.RootPackageIds.ToArray()),
            DependencyOfPackageIds = Array.AsReadOnly(pair.Value.DependencyOfPackageIds.ToArray())
        }, StringComparer.OrdinalIgnoreCase);
        return new ReadOnlyDictionary<string, ActivePackageDescriptor>(copied);
    }

    private static IReadOnlyDictionary<string, GraphActivationRecord> CopyActivationHistory(
        Dictionary<string, GraphActivationRecord>? activations)
    {
        var copied = (activations ?? []).ToDictionary(static pair => pair.Key, static pair => pair.Value with
        {
            RootPackageIds = Array.AsReadOnly(pair.Value.RootPackageIds.ToArray()),
            NodePackageIds = Array.AsReadOnly(pair.Value.NodePackageIds.ToArray()),
            NodeVersionsByPackageId = pair.Value.NodeVersionsByPackageId is null
                ? null
                : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(
                    pair.Value.NodeVersionsByPackageId, StringComparer.OrdinalIgnoreCase)),
            Failure = pair.Value.Failure is null ? null : pair.Value.Failure with
            {
                CyclePath = pair.Value.Failure.CyclePath is null
                    ? null
                    : Array.AsReadOnly(pair.Value.Failure.CyclePath.ToArray())
            }
        }, StringComparer.OrdinalIgnoreCase);
        return new ReadOnlyDictionary<string, GraphActivationRecord>(copied);
    }
}

internal sealed class PackageStoreEnrollmentPreviewSnapshot
{
    internal PackageStoreEnrollmentPreviewSnapshot(
        string rootLabel,
        PhysicalRootIdentity? rootIdentity,
        PackageStoreEnrollmentPreviewMembership? membership,
        string? ledgerDigest,
        bool isNonQuiescentObservation,
        IEnumerable<PackageStoreEnrollmentPreviewMember> members,
        IEnumerable<string>? diagnostics = null,
        long? enrollmentEpoch = null,
        IEnumerable<string>? currentMemberIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLabel);
        ArgumentNullException.ThrowIfNull(members);
        RootLabel = rootLabel;
        RootIdentity = rootIdentity is null ? null : new PhysicalRootIdentity(new PhysicalFileIdentity(
            rootIdentity.HandleIdentity.Provider, rootIdentity.HandleIdentity.VolumeOrDeviceId, rootIdentity.HandleIdentity.FileId));
        Membership = membership;
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        EnrollmentEpoch = enrollmentEpoch;
        LedgerDigest = ledgerDigest;
        IsNonQuiescentObservation = isNonQuiescentObservation;
        Members = Array.AsReadOnly(members.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        CurrentMemberIds = membership == PackageStoreEnrollmentPreviewMembership.Unenrolled
            ? Array.Empty<string>()
            : currentMemberIds is null ? null : Array.AsReadOnly(currentMemberIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
    }

    internal string RootLabel { get; }
    internal PhysicalRootIdentity? RootIdentity { get; }
    internal PackageStoreEnrollmentPreviewMembership? Membership { get; }
    internal long? EnrollmentEpoch { get; }
    internal string? LedgerDigest { get; }
    internal bool IsNonQuiescentObservation { get; }
    internal IReadOnlyList<PackageStoreEnrollmentPreviewMember> Members { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
    internal IReadOnlyList<string>? CurrentMemberIds { get; }
}

internal interface IPackageStoreEnrollmentPreviewService
{
    Task<PackageStoreEnrollmentPreviewSnapshot> PreviewRootAsync(string? rootLabel, CancellationToken cancellationToken);
}
