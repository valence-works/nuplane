using System.Collections.ObjectModel;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Represents either an unknown graph closure or a copied, explicitly known graph set.</summary>
/// <remarks>
/// Unknown values have a reason code and no graph collection. Known values always have a collection;
/// an empty collection is the only representation of KnownEmpty. These records describe evidence and
/// do not authorize package reads, enrollment, or deletion.
/// </remarks>
public sealed class PackageProtectionClosure
{
    /// <summary>Creates and validates a tagged protection closure.</summary>
    /// <param name="knowledge">Whether the closure is unknown or explicitly known.</param>
    /// <param name="unknownReason">The stable reason code required for an unknown closure.</param>
    /// <param name="graphs">The graph snapshots for a known closure; null for an unknown closure.</param>
    /// <exception cref="ArgumentException">The tag and payload do not form a valid variant.</exception>
    /// <exception cref="ArgumentNullException">A known closure's graph collection is null.</exception>
    internal PackageProtectionClosure(
        PackageProtectionClosureKnowledge knowledge,
        PackageProtectionUnknownReasonCode? unknownReason,
        IEnumerable<ProtectedGraphSnapshot>? graphs)
    {
        if (!Enum.IsDefined(knowledge))
            throw new ArgumentOutOfRangeException(nameof(knowledge));

        switch (knowledge)
        {
            case PackageProtectionClosureKnowledge.Unknown:
                if (unknownReason is null || !Enum.IsDefined(unknownReason.Value) || graphs is not null)
                    throw new ArgumentException("An unknown closure requires one known reason code and no graph collection.");
                UnknownReason = unknownReason;
                Graphs = null;
                break;

            case PackageProtectionClosureKnowledge.Known:
                if (unknownReason is not null)
                    throw new ArgumentException("A known closure cannot carry an unknown reason code.");
                ArgumentNullException.ThrowIfNull(graphs);
                var sourceGraphs = graphs.ToArray();
                if (sourceGraphs.Any(static graph => graph is null))
                    throw new ArgumentException("A known closure cannot contain null graph snapshots.", nameof(graphs));
                var copiedGraphs = sourceGraphs.Select(static graph => graph.Copy()).ToArray();
                if (copiedGraphs.Select(static graph => graph.SnapshotId).Distinct().Count() != copiedGraphs.Length)
                    throw new ArgumentException("A known closure cannot contain duplicate graph snapshot identities.", nameof(graphs));
                UnknownReason = null;
                Graphs = new ReadOnlyCollection<ProtectedGraphSnapshot>(copiedGraphs);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(knowledge));
        }

        Knowledge = knowledge;
    }

    /// <summary>Gets whether this closure is unknown or known.</summary>
    public PackageProtectionClosureKnowledge Knowledge { get; }

    /// <summary>Gets the stable reason for an unknown closure; null for a known closure.</summary>
    public PackageProtectionUnknownReasonCode? UnknownReason { get; }

    /// <summary>
    /// Gets the copied graph snapshots for a known closure; null for an unknown closure.
    /// An empty list means KnownEmpty.
    /// </summary>
    public IReadOnlyList<ProtectedGraphSnapshot>? Graphs { get; }

    internal PackageProtectionClosure Copy()
        => new(Knowledge, UnknownReason, Graphs);

    internal bool HasSamePayloadAs(PackageProtectionClosure other)
        => Knowledge == other.Knowledge && UnknownReason == other.UnknownReason &&
           (Graphs is null
               ? other.Graphs is null
               : other.Graphs is not null && Graphs.Count == other.Graphs.Count &&
                 Graphs.OrderBy(static graph => graph.SnapshotId)
                     .Zip(other.Graphs.OrderBy(static graph => graph.SnapshotId))
                     .All(static pair => pair.First.HasSamePayloadAs(pair.Second)));
}
