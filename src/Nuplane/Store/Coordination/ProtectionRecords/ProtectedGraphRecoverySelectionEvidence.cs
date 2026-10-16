using System.Collections.ObjectModel;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes the durable source that keeps a graph selectable during recovery.</summary>
/// <remarks>This is descriptive evidence; core must verify it against the saved recovery policy.</remarks>
public sealed class ProtectedGraphRecoverySelectionEvidence
{
    /// <summary>Creates recovery-selection evidence for one graph snapshot.</summary>
    /// <param name="recoveryPolicyId">The stable, versioned identity of the recovery policy.</param>
    /// <param name="sourceRevision">The positive state revision containing the source evidence.</param>
    /// <param name="selectedRootNodeIds">The graph nodes selected for the original requested roots.</param>
    /// <exception cref="ArgumentException">The source revision is invalid or a selected node identity is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selectedRootNodeIds"/> is null.</exception>
    internal ProtectedGraphRecoverySelectionEvidence(
        string recoveryPolicyId,
        long sourceRevision,
        IEnumerable<Guid> selectedRootNodeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPolicyId);
        if (sourceRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRevision));
        ArgumentNullException.ThrowIfNull(selectedRootNodeIds);

        var copiedNodeIds = selectedRootNodeIds.ToArray();
        if (copiedNodeIds.Length == 0 || copiedNodeIds.Any(static nodeId => nodeId == Guid.Empty))
            throw new ArgumentException("Recovery evidence requires non-empty selected root node identities.", nameof(selectedRootNodeIds));

        RecoveryPolicyId = recoveryPolicyId;
        SourceRevision = sourceRevision;
        SelectedRootNodeIds = new ReadOnlyCollection<Guid>(copiedNodeIds);
    }

    /// <summary>Gets the stable, versioned identity of the recovery policy.</summary>
    public string RecoveryPolicyId { get; }

    /// <summary>Gets the state revision containing the recovery-selection evidence.</summary>
    public long SourceRevision { get; }

    /// <summary>Gets the copied node identities selected for the graph's original requested roots.</summary>
    public IReadOnlyList<Guid> SelectedRootNodeIds { get; }

    internal ProtectedGraphRecoverySelectionEvidence Copy()
        => new(RecoveryPolicyId, SourceRevision, SelectedRootNodeIds);

    internal bool HasSamePayloadAs(ProtectedGraphRecoverySelectionEvidence other)
        => string.Equals(RecoveryPolicyId, other.RecoveryPolicyId, StringComparison.Ordinal) &&
            SourceRevision == other.SourceRevision &&
            ProtectionRecordValueCopies.HaveSameItems(SelectedRootNodeIds, other.SelectedRootNodeIds);
}
