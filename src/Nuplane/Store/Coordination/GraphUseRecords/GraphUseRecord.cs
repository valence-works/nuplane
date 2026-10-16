using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.GraphUseRecords;

/// <summary>Describes one graph-use candidate protected by an operating-system sentinel.</summary>
/// <remarks>
/// This immutable value is persistence data only. It does not prove that the sentinel is held,
/// that any root is live, or that the graph is complete or safe to read.
/// </remarks>
internal sealed class GraphUseRecord
{
    internal const int CurrentSchemaVersion = 1;

    internal GraphUseRecord(
        int schemaVersion,
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        Guid useId,
        ProtectedGraphSnapshot graphSnapshot,
        PackageGraphUseSnapshotState snapshotState,
        PhysicalFileIdentity sentinelIdentity,
        GraphUseLifetimeKind lifetimeKind,
        int diagnosticProcessId,
        string payloadDigest)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The graph-use schema version is unsupported.");
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (useId == Guid.Empty)
            throw new ArgumentException("A graph-use identifier cannot be empty.", nameof(useId));
        ArgumentNullException.ThrowIfNull(graphSnapshot);
        if (graphSnapshot.Disposition != ProtectedGraphDisposition.Active || graphSnapshot.RecoverySelectionEvidence is not null)
            throw new ArgumentException("A graph-use record requires an active-only snapshot without recovery evidence.", nameof(graphSnapshot));
        if (!Enum.IsDefined(snapshotState))
            throw new ArgumentOutOfRangeException(nameof(snapshotState));
        ArgumentNullException.ThrowIfNull(sentinelIdentity);
        if (!Enum.IsDefined(lifetimeKind))
            throw new ArgumentOutOfRangeException(nameof(lifetimeKind));
        if (diagnosticProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(diagnosticProcessId));
        ProtectionDigest.ValidateCanonicalDigest(payloadDigest);

        var copiedRoot = ProtectionRecordValueCopies.CopyRoot(rootIdentity);
        var copiedSentinel = ProtectionRecordValueCopies.CopyIdentity(sentinelIdentity);
        var rootHandle = copiedRoot.HandleIdentity;
        if (!SameVolume(rootHandle, copiedSentinel))
            throw new ArgumentException("The sentinel identity must be on the graph-use root's native provider and volume.", nameof(sentinelIdentity));
        if (rootHandle == copiedSentinel)
            throw new ArgumentException("The sentinel identity must identify a file distinct from the directory root.", nameof(sentinelIdentity));

        var copiedGraph = graphSnapshot.Copy();
        if (!copiedGraph.Roots.Contains(copiedRoot))
            throw new ArgumentException("The graph-use root must occur in the full graph snapshot.", nameof(graphSnapshot));
        foreach (var node in copiedGraph.Nodes)
        {
            if (!SameVolume(node.Install.Root.HandleIdentity, node.Install.DirectoryIdentity))
                throw new ArgumentException("A graph install directory must share its physical root's provider and volume.", nameof(graphSnapshot));
        }

        SchemaVersion = schemaVersion;
        RootIdentity = copiedRoot;
        EnrollmentEpoch = enrollmentEpoch;
        UseId = useId;
        GraphSnapshot = copiedGraph;
        SnapshotState = snapshotState;
        SentinelIdentity = copiedSentinel;
        LifetimeKind = lifetimeKind;
        DiagnosticProcessId = diagnosticProcessId;
        PayloadDigest = payloadDigest;
    }

    internal int SchemaVersion { get; }
    internal PhysicalRootIdentity RootIdentity { get; }
    internal long EnrollmentEpoch { get; }
    internal Guid UseId { get; }
    internal ProtectedGraphSnapshot GraphSnapshot { get; }
    internal PackageGraphUseSnapshotState SnapshotState { get; }
    internal PhysicalFileIdentity SentinelIdentity { get; }
    internal GraphUseLifetimeKind LifetimeKind { get; }
    internal int DiagnosticProcessId { get; }
    internal string PayloadDigest { get; }

    internal static GraphUseRecord Create(
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        Guid useId,
        ProtectedGraphSnapshot graphSnapshot,
        PackageGraphUseSnapshotState snapshotState,
        PhysicalFileIdentity sentinelIdentity,
        GraphUseLifetimeKind lifetimeKind,
        int diagnosticProcessId)
    {
        var candidate = new GraphUseRecord(CurrentSchemaVersion, rootIdentity, enrollmentEpoch, useId, graphSnapshot,
            snapshotState, sentinelIdentity, lifetimeKind, diagnosticProcessId, new string('0', 64));
        return new GraphUseRecord(CurrentSchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch, candidate.UseId,
            candidate.GraphSnapshot, candidate.SnapshotState, candidate.SentinelIdentity, candidate.LifetimeKind,
            candidate.DiagnosticProcessId, ProtectionDigest.GraphUse(candidate));
    }

    private static bool SameVolume(PhysicalFileIdentity left, PhysicalFileIdentity right)
        => StringComparer.Ordinal.Equals(left.Provider, right.Provider) &&
            StringComparer.Ordinal.Equals(left.VolumeOrDeviceId, right.VolumeOrDeviceId);
}
