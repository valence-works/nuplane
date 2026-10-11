using System.Collections.ObjectModel;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes a tagged v2 graph closure with common-generation evidence.</summary>
/// <remarks>Known with an empty graph list is the only KnownEmpty representation; values grant no authority.</remarks>
public sealed class PackageProtectionClosureV2
{
    internal PackageProtectionClosureV2(
        PackageProtectionClosureKnowledge knowledge,
        PackageProtectionUnknownReasonCode? unknownReason,
        IEnumerable<ProtectedGraphSnapshotV2>? graphs)
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
                var items = graphs.ToArray();
                if (items.Any(static graph => graph is null))
                    throw new ArgumentException("A known closure cannot contain null graph snapshots.", nameof(graphs));
                var copied = items.Select(static graph => graph.Copy()).ToArray();
                if (copied.Select(static graph => graph.SnapshotId).Distinct().Count() != copied.Length)
                    throw new ArgumentException("A known closure cannot contain duplicate graph snapshot identities.", nameof(graphs));
                UnknownReason = null;
                Graphs = new ReadOnlyCollection<ProtectedGraphSnapshotV2>(copied);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(knowledge));
        }

        Knowledge = knowledge;
    }

    /// <summary>Gets whether this closure is unknown or known.</summary>
    public PackageProtectionClosureKnowledge Knowledge { get; }

    /// <summary>Gets the stable reason code for an unknown closure; null for a known closure.</summary>
    public PackageProtectionUnknownReasonCode? UnknownReason { get; }

    /// <summary>Gets the copied graph list for a known closure; null for unknown, empty for KnownEmpty.</summary>
    public IReadOnlyList<ProtectedGraphSnapshotV2>? Graphs { get; }

    internal PackageProtectionClosureV2 Copy() => new(Knowledge, UnknownReason, Graphs);

    internal bool HasSamePayloadAs(PackageProtectionClosureV2 other)
        => Knowledge == other.Knowledge && UnknownReason == other.UnknownReason &&
           (Graphs is null
               ? other.Graphs is null
               : other.Graphs is not null && Graphs.Count == other.Graphs.Count &&
                 Graphs.OrderBy(static graph => graph.SnapshotId)
                     .Zip(other.Graphs.OrderBy(static graph => graph.SnapshotId))
                     .All(static pair => pair.First.HasSamePayloadAs(pair.Second)));
}
