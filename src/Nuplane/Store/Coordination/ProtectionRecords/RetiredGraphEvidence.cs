namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Records why a graph snapshot stopped being selectable by recovery.</summary>
/// <remarks>
/// This value is historical evidence, not permission to delete its former installs. Core verifies
/// the proof against the durable transition and all other protection records before retirement is used.
/// </remarks>
public sealed class RetiredGraphEvidence
{
    /// <summary>Creates retirement evidence for one previously protected graph snapshot.</summary>
    /// <param name="snapshotId">The snapshot identity being retired.</param>
    /// <param name="graphId">The deterministic graph identity.</param>
    /// <param name="generationId">The graph generation identity.</param>
    /// <param name="retiringEpoch">The positive membership epoch that records retirement.</param>
    /// <param name="retiringRevision">The positive state revision that records retirement.</param>
    /// <param name="reason">The explicit reason the graph is no longer recoverable.</param>
    /// <param name="proofDigest">A non-empty digest of the durable transition or operator proof.</param>
    /// <exception cref="ArgumentException">An identity or proof digest is empty.</exception>
    internal RetiredGraphEvidence(
        Guid snapshotId,
        string graphId,
        string generationId,
        long retiringEpoch,
        long retiringRevision,
        RetiredGraphReason reason,
        string proofDigest)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("A retired graph snapshot identifier cannot be empty.", nameof(snapshotId));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        if (retiringEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(retiringEpoch));
        if (retiringRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(retiringRevision));
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(proofDigest);

        SnapshotId = snapshotId;
        GraphId = graphId;
        GenerationId = generationId;
        RetiringEpoch = retiringEpoch;
        RetiringRevision = retiringRevision;
        Reason = reason;
        ProofDigest = proofDigest;
    }

    /// <summary>Gets the retired graph snapshot identity.</summary>
    public Guid SnapshotId { get; }

    /// <summary>Gets the deterministic graph identity.</summary>
    public string GraphId { get; }

    /// <summary>Gets the graph generation identity.</summary>
    public string GenerationId { get; }

    /// <summary>Gets the membership epoch that records the retirement.</summary>
    public long RetiringEpoch { get; }

    /// <summary>Gets the state revision that records the retirement.</summary>
    public long RetiringRevision { get; }

    /// <summary>Gets the explicit reason the graph is no longer recoverable.</summary>
    public RetiredGraphReason Reason { get; }

    /// <summary>Gets the digest of durable recovery metadata or operator evidence that proves retirement.</summary>
    public string ProofDigest { get; }

    internal RetiredGraphEvidence Copy()
        => new(SnapshotId, GraphId, GenerationId, RetiringEpoch, RetiringRevision, Reason, ProofDigest);

    internal bool HasSamePayloadAs(RetiredGraphEvidence other)
        => SnapshotId == other.SnapshotId &&
           string.Equals(GraphId, other.GraphId, StringComparison.Ordinal) &&
           string.Equals(GenerationId, other.GenerationId, StringComparison.Ordinal) &&
           RetiringEpoch == other.RetiringEpoch && RetiringRevision == other.RetiringRevision &&
           Reason == other.Reason && string.Equals(ProofDigest, other.ProofDigest, StringComparison.Ordinal);
}
