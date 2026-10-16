using System.Collections.ObjectModel;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes the common state generation that selected one graph for recovery.</summary>
/// <remarks>This v2 value is descriptive persisted evidence, not a recovery or package-use capability.</remarks>
public sealed class ProtectedGraphRecoverySelectionEvidenceV2
{
    internal ProtectedGraphRecoverySelectionEvidenceV2(
        string recoveryPolicyId,
        long sourceGeneration,
        IEnumerable<Guid> selectedRootNodeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPolicyId);
        if (sourceGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        ArgumentNullException.ThrowIfNull(selectedRootNodeIds);

        var copiedNodeIds = selectedRootNodeIds.ToArray();
        if (copiedNodeIds.Length == 0 || copiedNodeIds.Any(static nodeId => nodeId == Guid.Empty))
            throw new ArgumentException("Recovery evidence requires non-empty selected root node identities.", nameof(selectedRootNodeIds));

        RecoveryPolicyId = recoveryPolicyId;
        SourceGeneration = sourceGeneration;
        SelectedRootNodeIds = new ReadOnlyCollection<Guid>(copiedNodeIds);
    }

    /// <summary>Gets the stable, versioned identity of the recovery policy.</summary>
    public string RecoveryPolicyId { get; }

    /// <summary>Gets the common state generation containing the recovery-selection evidence.</summary>
    public long SourceGeneration { get; }

    /// <summary>Gets the copied selected node identities for the original requested roots.</summary>
    public IReadOnlyList<Guid> SelectedRootNodeIds { get; }

    internal ProtectedGraphRecoverySelectionEvidenceV2 Copy()
        => new(RecoveryPolicyId, SourceGeneration, SelectedRootNodeIds);

    internal bool HasSamePayloadAs(ProtectedGraphRecoverySelectionEvidenceV2 other)
        => string.Equals(RecoveryPolicyId, other.RecoveryPolicyId, StringComparison.Ordinal) &&
           SourceGeneration == other.SourceGeneration &&
           ProtectionRecordValueCopies.HaveSameItems(SelectedRootNodeIds, other.SelectedRootNodeIds);
}
