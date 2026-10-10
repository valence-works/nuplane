using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Computes canonical integrity digests for persisted store protection and membership values.</summary>
/// <remarks>
/// These hashes bind descriptive persisted values; they do not establish membership, graph
/// completeness, filesystem identity, admission, or deletion authority. Revision 1 uses the fixed
/// tag tables below and invariant uppercase for case-insensitive identifiers and map keys.
/// </remarks>
internal static class ProtectionDigest
{
    private const uint EncodingRevision = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("NUPLANE-CANONICAL\0");

    /// <summary>Hashes every legacy state-body field, excluding both optional protection properties.</summary>
    internal static string StateBody(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = StoreStateSerializer.Normalize(state);
        ArgumentNullException.ThrowIfNull(normalized.ActiveVersionById);
        ArgumentNullException.ThrowIfNull(normalized.LastKnownGoodById);
        ArgumentNullException.ThrowIfNull(normalized.LastFailureById);
        ArgumentNullException.ThrowIfNull(normalized.LastSuccessfulSourceSnapshots);

        return Digest("StoreStateBody", writer =>
        {
            writer.Field(1, EncodeMap(normalized.ActiveVersionById, EncodeString));
            writer.Field(2, EncodeMap(normalized.LastKnownGoodById, EncodeString));
            writer.Field(3, EncodeMap(normalized.LastFailureById, EncodeFailure));
            writer.Field(4, EncodeMap(normalized.LastSuccessfulSourceSnapshots, EncodeSourceSnapshot));
            writer.Field(5, EncodeTimestamp(normalized.UpdatedAt));
            writer.Field(6, EncodeMap(normalized.ActivePackageDescriptorsByIdNormalized, EncodeDescriptor));
            writer.Field(7, EncodeMap(normalized.ActiveGraphsByIdNormalized, EncodeActivationGraph));
        });
    }

    /// <summary>Hashes a protection payload while omitting only its stored protection digest.</summary>
    internal static string Protection(PackageProtectionRecord protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        return Digest("PackageProtection", writer => EncodeProtectionFields(writer, protection, includeStoredDigest: false));
    }

    /// <summary>Hashes the fixed v2 participant set independently of root-local epochs and revisions.</summary>
    internal static string PackageProtectionParticipantSet(Guid logicalMemberId, IEnumerable<PhysicalRootIdentity> roots)
    {
        if (logicalMemberId == Guid.Empty)
            throw new ArgumentException("A logical member identity cannot be empty.", nameof(logicalMemberId));
        ArgumentNullException.ThrowIfNull(roots);
        var copiedRoots = roots.Select(ProtectionRecordValueCopies.CopyRoot)
            .OrderBy(static root => root, PhysicalRootIdentityComparer.Instance).ToArray();
        if (copiedRoots.Length == 0 || copiedRoots.Distinct().Count() != copiedRoots.Length)
            throw new ArgumentException("A participant set requires unique physical roots.", nameof(roots));

        return Digest("NuplaneParticipantSetV2", writer =>
        {
            writer.Field(1, GuidBytes(logicalMemberId));
            writer.Field(2, EncodeSequence(copiedRoots, EncodePhysicalRoot));
        });
    }

    /// <summary>Hashes a v2 root row while omitting only its stored row digest.</summary>
    internal static string PackageProtectionBundleRow(PackageProtectionBundleRootRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Digest("PackageProtectionBundleRootRowV2", writer =>
        {
            writer.Field(1, EncodeInt32(Nuplane.Store.Coordination.ProtectionRecords.PackageProtectionBundle.CurrentSchemaVersion));
            writer.Field(2, EncodePhysicalRoot(row.RootIdentity));
            writer.Field(3, EncodeInt64(row.EnrollmentEpoch));
            writer.Field(4, EncodeString(row.MemberId));
            writer.Field(5, EncodeInt64(row.Revision));
            writer.Field(6, EncodeInt64(row.StateGeneration));
            writer.Field(7, DecodeDigest(row.StateBodyDigest));
            writer.Field(8, EncodeProtectionClosureV2(row.ActiveClosure));
            writer.Field(9, EncodeProtectionClosureV2(row.RecoverableClosure));
            writer.Field(10, EncodeSequence(row.RetiredGraphs
                .OrderBy(static item => GuidBytes(item.SnapshotId), ByteArrayComparer.Instance), EncodeRetiredGraph));
            writer.Field(11, EncodeBoolean(row.LegacyUnknownRecovery));
        });
    }

    /// <summary>Hashes the common v2 envelope and ordered root-row digest references.</summary>
    internal static string PackageProtectionBundle(PackageProtectionBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return Digest("NuplaneProtectionBundleV2", writer =>
        {
            writer.Field(1, EncodeInt32(bundle.SchemaVersion));
            writer.Field(2, GuidBytes(bundle.LogicalMemberId));
            writer.Field(3, GuidBytes(bundle.PublicationId));
            writer.Field(4, EncodeInt64(bundle.StateGeneration));
            writer.Field(5, DecodeDigest(bundle.StateBodyDigest));
            writer.Field(6, DecodeDigest(bundle.ParticipantSetDigest));
            writer.Field(7, EncodeSequence(bundle.Rows, EncodeProtectionBundleRootRowReference));
        });
    }

    /// <summary>Hashes a root-membership ledger while omitting only its stored ledger digest.</summary>
    internal static string Ledger(RootMembershipRecord ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return Digest("RootMembershipLedger", writer =>
        {
            writer.Field(1, EncodeInt32(ledger.SchemaVersion));
            writer.Field(2, EncodePhysicalRoot(ledger.RootIdentity));
            writer.Field(3, EncodeInt64(ledger.EnrollmentEpoch));
            writer.Field(4, EncodeEnum(ledger.Status));
            writer.Field(5, EncodeSequence(ledger.Members, EncodeMember));
            writer.Field(6, EncodeSequence(ledger.TargetMemberIds, EncodeString));
            writer.Field(7, EncodeSequence(ledger.RetiredMembers, EncodeRetiredMember));
            writer.Field(8, EncodeNullableRecord(ledger.PendingStateCommit, EncodePendingCommit));
            // Schema 1's canonical ledger recipe is frozen. Schema 2 adds its group-pending slot
            // under a new tag, leaving every schema-1 digest byte-for-byte unchanged.
            if (ledger.SchemaVersion >= RootMembershipRecord.BundleSchemaVersion)
                writer.Field(9, EncodeNullableRecord(ledger.PendingGroupPublicationV2, EncodePendingGroupPublicationV2));
        });
    }

    internal static string GroupPublicationIntentV2(GroupPublicationDescriptorV2 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Digest("NuplaneGroupPublicationIntentV2", writer =>
            writer.Field(1, EncodeGroupPublicationDescriptorV2(descriptor)));
    }

    internal static string GroupPublicationBoundV2(
        GroupPublicationDescriptorV2 descriptor,
        PhysicalFileIdentity stagedStateFileIdentity,
        PhysicalFileIdentity? backupStateFileIdentity)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(stagedStateFileIdentity);
        return Digest("NuplaneGroupPublicationBoundV2", writer =>
        {
            writer.Field(1, DecodeDigest(descriptor.IntentDigest));
            writer.Field(2, EncodePhysicalFile(stagedStateFileIdentity));
            writer.Field(3, EncodeNullableRecord(backupStateFileIdentity, EncodePhysicalFile));
        });
    }

    internal static string GroupPublicationDecisionV2(
        GroupPublicationDescriptorV2 descriptor,
        string? boundCommitDigest,
        GroupPublicationResolutionV2 resolution)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (resolution == GroupPublicationResolutionV2.Unresolved || !Enum.IsDefined(resolution))
            throw new ArgumentOutOfRangeException(nameof(resolution));
        if (resolution == GroupPublicationResolutionV2.Next && boundCommitDigest is null)
            throw new ArgumentException("Next requires the bound-commit digest.", nameof(boundCommitDigest));
        if (boundCommitDigest is not null)
            ValidateCanonicalDigest(boundCommitDigest);
        return Digest("NuplaneGroupPublicationResolutionV2", writer =>
        {
            writer.Field(1, DecodeDigest(descriptor.IntentDigest));
            writer.Field(2, EncodeNullableString(boundCommitDigest));
            writer.Field(3, EncodeEnum(resolution));
        });
    }

    /// <summary>Hashes the native identity of one validated package completion marker.</summary>
    /// <remarks>This is descriptive identity evidence; it does not hash marker or package contents.</remarks>
    internal static string PackageInstallCompletionIdentity(PhysicalFileIdentity markerIdentity)
    {
        ArgumentNullException.ThrowIfNull(markerIdentity);
        return Digest("PackageInstallCompletionIdentity", writer =>
            writer.Field(1, EncodePhysicalFile(markerIdentity)));
    }

    /// <summary>Hashes every descriptive graph-use record field except its stored digest.</summary>
    internal static string GraphUse(GraphUseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Digest("GraphUseRecord", writer =>
        {
            writer.Field(1, EncodeInt32(record.SchemaVersion));
            writer.Field(2, EncodePhysicalRoot(record.RootIdentity));
            writer.Field(3, EncodeInt64(record.EnrollmentEpoch));
            writer.Field(4, GuidBytes(record.UseId));
            writer.Field(5, EncodeProtectedGraph(record.GraphSnapshot));
            writer.Field(6, EncodeEnum(record.SnapshotState));
            writer.Field(7, EncodePhysicalFile(record.SentinelIdentity));
            writer.Field(8, EncodeEnum(record.LifetimeKind));
            writer.Field(9, EncodeInt32(record.DiagnosticProcessId));
        });
    }

    /// <summary>Derives a stable identity for a selected persisted graph subclosure.</summary>
    /// <remarks>
    /// The versioned domain intentionally differs from the original resolved-graph identity:
    /// historical source decisions and target-framework inputs are not reconstructible from a
    /// persisted snapshot. Repeated requests and edges remain repeated in the canonical payload.
    /// </remarks>
    internal static string RetainedSubclosureGraphId(
        string parentGraphId,
        string parentGenerationId,
        IEnumerable<PackageGraphRootSelection> requestedRoots,
        IEnumerable<PackageGraphNodeIdentity> nodes,
        IEnumerable<PackageGraphEdgeIdentity> edges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentGraphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentGenerationId);
        ArgumentNullException.ThrowIfNull(requestedRoots);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        return Digest("RetainedGraphSubclosureV1", writer =>
        {
            writer.Field(1, EncodeString(parentGraphId));
            writer.Field(2, EncodeString(parentGenerationId));
            writer.Field(3, EncodeSequence(requestedRoots
                .Select(EncodeRootSelection)
                .OrderBy(static value => value, ByteArrayComparer.Instance), static value => value));
            writer.Field(4, EncodeSequence(nodes
                .Select(EncodeProtectedNode)
                .OrderBy(static value => value, ByteArrayComparer.Instance), static value => value));
            writer.Field(5, EncodeSequence(edges
                .Select(EncodeProtectedEdge)
                .OrderBy(static value => value, ByteArrayComparer.Instance), static value => value));
        });
    }

    internal static void ValidateCanonicalDigest(string value)
    {
        _ = DecodeDigest(value);
    }

    private static string Digest(string domain, Action<CanonicalWriter> encodeFields)
    {
        using var writer = new CanonicalWriter();
        writer.Bytes(Prefix);
        writer.String(domain);
        writer.UInt32(EncodingRevision);
        encodeFields(writer);
        return Convert.ToHexString(SHA256.HashData(writer.ToArray())).ToLowerInvariant();
    }

    private static string FoldIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var folded = value.ToUpperInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(value, folded))
            throw new ArgumentException("The identifier has no compatible invariant-uppercase canonical form.", nameof(value));
        return folded;
    }

    private static byte[] EncodeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StrictUtf8.GetBytes(value);
    }

    private static byte[] EncodeBoolean(bool value) => [value ? (byte)1 : (byte)0];

    private static byte[] EncodeInt32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeInt64(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeEnum<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined enum values cannot be digested.");
        return EncodeInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static byte[] EncodeTimestamp(DateTimeOffset value)
    {
        using var writer = new CanonicalWriter();
        writer.Int64(value.UtcDateTime.Ticks);
        writer.Int64(value.Offset.Ticks);
        return writer.ToArray();
    }

    private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N", CultureInfo.InvariantCulture));

    private static byte[] DecodeDigest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A canonical SHA-256 digest must contain 64 lowercase hexadecimal characters.", nameof(value));
        }

        return Convert.FromHexString(value);
    }

    private static byte[] EncodeMap<TValue>(IReadOnlyDictionary<string, TValue> map, Func<TValue, byte[]> valueEncoder)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(valueEncoder);

        var entries = new List<(string Key, TValue Value)>();
        var comparerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foldedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            if (!comparerKeys.Add(pair.Key))
                throw new ArgumentException("A digest map contains keys that collide under OrdinalIgnoreCase.", nameof(map));

            var key = FoldIdentifier(pair.Key);
            if (!foldedKeys.Add(key))
                throw new ArgumentException("Distinct digest-map keys collide after invariant-uppercase canonicalization.", nameof(map));
            entries.Add((key, pair.Value));
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        using var writer = new CanonicalWriter();
        writer.Count(entries.Count);
        foreach (var entry in entries)
        {
            using var encodedEntry = new CanonicalWriter();
            encodedEntry.String(entry.Key);
            encodedEntry.LengthPrefixed(valueEncoder(entry.Value));
            writer.LengthPrefixed(encodedEntry.ToArray());
        }

        return writer.ToArray();
    }

    private static byte[] EncodeSequence<TValue>(IEnumerable<TValue> values, Func<TValue, byte[]> valueEncoder)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(valueEncoder);
        var items = values.ToArray();
        using var writer = new CanonicalWriter();
        writer.Count(items.Length);
        foreach (var item in items)
            writer.LengthPrefixed(valueEncoder(item));
        return writer.ToArray();
    }

    private static byte[] EncodeNullableSequence<TValue>(IReadOnlyList<TValue>? values, Func<TValue, byte[]> valueEncoder)
    {
        if (values is null)
            return [0];
        using var writer = new CanonicalWriter();
        writer.Byte(1);
        writer.Bytes(EncodeSequence(values, valueEncoder));
        return writer.ToArray();
    }

    private static byte[] EncodeNullableString(string? value)
        => value is null ? [0] : [1, .. EncodeString(value)];

    private static byte[] EncodeNullableRecord<TValue>(TValue? value, Func<TValue, byte[]> valueEncoder) where TValue : class
        => value is null ? [0] : [1, .. valueEncoder(value)];

    private static byte[] EncodeNullableMap(IReadOnlyDictionary<string, string>? map)
        => map is null ? [0] : [1, .. EncodeMap(map, EncodeString)];

    private static byte[] EncodeFailure(FailureRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(FoldIdentifier(value.PackageId)));
            writer.Field(2, EncodeString(value.Stage));
            writer.Field(3, EncodeString(value.Message));
            writer.Field(4, EncodeTimestamp(value.OccurredAt));
            writer.Field(5, EncodeString(value.CorrelationId));
        });
    }

    private static byte[] EncodeSourceSnapshot(SourceSnapshotRef value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.Version));
            writer.Field(2, EncodeTimestamp(value.CapturedAt));
            writer.Field(3, EncodeNullableSequence(value.Requests, EncodePackageRequest));
        });
    }

    private static byte[] EncodePackageRequest(PackageRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(FoldIdentifier(value.Id)));
            writer.Field(2, EncodeString(value.VersionRange));
            writer.Field(3, EncodeNullableString(value.FeedName));
            writer.Field(4, EncodeEnum(value.UpdatePolicy));
            writer.Field(5, EncodeString(value.SourceName));
        });
    }

    private static byte[] EncodeDescriptor(ActivePackageDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(FoldIdentifier(value.PackageId)));
            writer.Field(2, EncodeString(value.Version));
            writer.Field(3, EncodeNullableString(value.FeedName));
            writer.Field(4, EncodeNullableString(value.SourceName));
            writer.Field(5, EncodeString(value.InstallPath));
            writer.Field(6, EncodeTimestamp(value.ActivatedAtUtc));
            writer.Field(7, EncodeString(value.ActivationCorrelationId));
            writer.Field(8, EncodeString(value.GraphId));
            writer.Field(9, EncodeString(value.GraphGenerationId));
            writer.Field(10, EncodeEnum(value.PackageRole));
            writer.Field(11, EncodeSequence(value.RootPackageIds, item => EncodeString(FoldIdentifier(item))));
            writer.Field(12, EncodeSequence(value.DependencyOfPackageIds, item => EncodeString(FoldIdentifier(item))));
            writer.Field(13, EncodeBoolean(value.Discoverable));
        });
    }

    private static byte[] EncodeActivationGraph(GraphActivationRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.GraphId));
            writer.Field(2, EncodeString(value.GenerationId));
            writer.Field(3, EncodeSequence(value.RootPackageIds, item => EncodeString(FoldIdentifier(item))));
            writer.Field(4, EncodeSequence(value.NodePackageIds, item => EncodeString(FoldIdentifier(item))));
            writer.Field(5, EncodeTimestamp(value.ActivatedAtUtc));
            writer.Field(6, EncodeString(value.CorrelationId));
            writer.Field(7, EncodeEnum(value.Status));
            writer.Field(8, EncodeNullableRecord(value.Failure, EncodeActivationFailure));
            writer.Field(9, EncodeNullableMap(value.NodeVersionsByPackageId));
        });
    }

    private static byte[] EncodeActivationFailure(GraphActivationFailure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.FailureStage));
            writer.Field(2, EncodeString(value.ReasonCode));
            writer.Field(3, EncodeString(value.Message));
            writer.Field(4, EncodeNullableSequence(value.CyclePath, EncodeString));
            writer.Field(5, EncodeNullableString(value.UnsupportedAssetPath));
        });
    }

    private static byte[] EncodeClosure(PackageProtectionClosure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeEnum(value.Knowledge));
            writer.Field(2, value.UnknownReason is null ? [0] : [1, .. EncodeEnum(value.UnknownReason.Value)]);
            writer.Field(3, value.Graphs is null
                ? [0]
                : [1, .. EncodeSequence(value.Graphs
                    .OrderBy(static graph => GuidBytes(graph.SnapshotId), ByteArrayComparer.Instance), EncodeProtectedGraph)]);
        });
    }

    private static byte[] EncodeProtectionClosureV2(PackageProtectionClosureV2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeInt32(Nuplane.Store.Coordination.ProtectionRecords.PackageProtectionBundle.CurrentSchemaVersion));
            writer.Field(2, EncodeEnum(value.Knowledge));
            writer.Field(3, value.UnknownReason is null ? [0] : [1, .. EncodeEnum(value.UnknownReason.Value)]);
            writer.Field(4, value.Graphs is null
                ? [0]
                : [1, .. EncodeSequence(value.Graphs
                    .OrderBy(static graph => GuidBytes(graph.SnapshotId), ByteArrayComparer.Instance), EncodeProtectedGraphV2)]);
        });
    }

    private static byte[] EncodeProtectedGraphV2(ProtectedGraphSnapshotV2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeInt32(Nuplane.Store.Coordination.ProtectionRecords.PackageProtectionBundle.CurrentSchemaVersion));
            writer.Field(2, GuidBytes(value.SnapshotId));
            writer.Field(3, EncodeString(value.GraphId));
            writer.Field(4, EncodeString(value.GenerationId));
            writer.Field(5, EncodeEnum(value.Disposition));
            writer.Field(6, EncodeSequence(value.Roots
                .OrderBy(static root => EncodePhysicalRoot(root), ByteArrayComparer.Instance), EncodePhysicalRoot));
            writer.Field(7, EncodeSequence(value.RequestedRoots
                .Select(EncodeRootSelection)
                .OrderBy(static selection => selection, ByteArrayComparer.Instance), static selection => selection));
            writer.Field(8, EncodeSequence(value.Nodes
                .Select(EncodeProtectedNode)
                .OrderBy(static node => node, ByteArrayComparer.Instance), static node => node));
            writer.Field(9, EncodeSequence(value.Edges
                .Select(EncodeProtectedEdge)
                .OrderBy(static edge => edge, ByteArrayComparer.Instance), static edge => edge));
            writer.Field(10, EncodeNullableRecord(value.RecoverySelectionEvidence, EncodeRecoveryEvidenceV2));
        });
    }

    private static byte[] EncodeRecoveryEvidenceV2(ProtectedGraphRecoverySelectionEvidenceV2 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeInt32(Nuplane.Store.Coordination.ProtectionRecords.PackageProtectionBundle.CurrentSchemaVersion));
            writer.Field(2, EncodeString(value.RecoveryPolicyId));
            writer.Field(3, EncodeInt64(value.SourceGeneration));
            writer.Field(4, EncodeSequence(value.SelectedRootNodeIds
                .OrderBy(GuidBytes, ByteArrayComparer.Instance), GuidBytes));
        });
    }

    private static byte[] EncodeProtectionBundleRootRowReference(PackageProtectionBundleRootRow value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(value.RootIdentity));
            writer.Field(2, EncodeInt64(value.EnrollmentEpoch));
            writer.Field(3, EncodeString(value.MemberId));
            writer.Field(4, EncodeInt64(value.Revision));
            writer.Field(5, DecodeDigest(value.ProtectionDigest));
        });
    }

    private static byte[] EncodeProtectedGraph(ProtectedGraphSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, GuidBytes(value.SnapshotId));
            writer.Field(2, EncodeString(value.GraphId));
            writer.Field(3, EncodeString(value.GenerationId));
            writer.Field(4, EncodeEnum(value.Disposition));
            writer.Field(5, EncodeSequence(value.Roots
                .OrderBy(static root => EncodePhysicalRoot(root), ByteArrayComparer.Instance), EncodePhysicalRoot));
            writer.Field(6, EncodeSequence(value.RequestedRoots
                .OrderBy(static selection => EncodeRootSelection(selection), ByteArrayComparer.Instance), EncodeRootSelection));
            writer.Field(7, EncodeSequence(value.Nodes
                .OrderBy(static node => GuidBytes(node.NodeId), ByteArrayComparer.Instance), EncodeProtectedNode));
            writer.Field(8, EncodeSequence(value.Edges
                .OrderBy(static edge => EncodeProtectedEdge(edge), ByteArrayComparer.Instance), EncodeProtectedEdge));
            writer.Field(9, EncodeNullableRecord(value.RecoverySelectionEvidence, EncodeRecoveryEvidence));
        });
    }

    private static byte[] EncodeRootSelection(PackageGraphRootSelection value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodePackageRequest(value.Request));
            writer.Field(2, GuidBytes(value.SelectedNodeId));
        });
    }

    private static byte[] EncodeProtectedNode(PackageGraphNodeIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, GuidBytes(value.NodeId));
            writer.Field(2, EncodeInstall(value.Install));
        });
    }

    private static byte[] EncodeProtectedEdge(PackageGraphEdgeIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, GuidBytes(value.FromNodeId));
            writer.Field(2, GuidBytes(value.ToNodeId));
            writer.Field(3, EncodeString(FoldIdentifier(value.RequestedPackageId)));
            writer.Field(4, EncodeString(value.RequestedVersionRange));
            writer.Field(5, EncodeString(value.TargetFramework));
            writer.Field(6, EncodeBoolean(value.IsOptional));
        });
    }

    private static byte[] EncodeInstall(PackageInstallIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(value.Root));
            writer.Field(2, EncodeString(FoldIdentifier(value.PackageId)));
            writer.Field(3, EncodeString(value.Version));
            writer.Field(4, EncodeString(value.RootRelativeInstallPath));
            writer.Field(5, EncodePhysicalFile(value.DirectoryIdentity));
            writer.Field(6, EncodeString(value.CompletionIdentity));
            writer.Field(7, EncodeNullableString(value.VerifiedArchiveHash));
        });
    }

    private static byte[] EncodeRecoveryEvidence(ProtectedGraphRecoverySelectionEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.RecoveryPolicyId));
            writer.Field(2, EncodeInt64(value.SourceRevision));
            writer.Field(3, EncodeSequence(value.SelectedRootNodeIds
                .OrderBy(GuidBytes, ByteArrayComparer.Instance), GuidBytes));
        });
    }

    private static byte[] EncodeRetiredGraph(RetiredGraphEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, GuidBytes(value.SnapshotId));
            writer.Field(2, EncodeString(value.GraphId));
            writer.Field(3, EncodeString(value.GenerationId));
            writer.Field(4, EncodeInt64(value.RetiringEpoch));
            writer.Field(5, EncodeInt64(value.RetiringRevision));
            writer.Field(6, EncodeEnum(value.Reason));
            writer.Field(7, DecodeDigest(value.ProofDigest));
        });
    }

    private static byte[] EncodeMember(RootMemberRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (tag, payload) = EncodeBinding(value.Binding);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.MemberId));
            writer.Field(2, EncodeString(value.ConfiguredLocator));
            writer.Field(3, EncodeInt32(tag));
            writer.Field(4, payload);
        });
    }

    private static (int Tag, byte[] Payload) EncodeBinding(RootMemberRecord.MemberBinding value)
        => value switch
        {
            RootMemberRecord.DeclaredBinding => (0, []),
            RootMemberRecord.ProspectiveBinding prospective => (1, EncodeProspectiveBinding(prospective)),
            RootMemberRecord.ExistingUnprotectedBinding unprotected => (2, EncodeExistingUnprotectedBinding(unprotected)),
            RootMemberRecord.AcknowledgedBinding acknowledged => (3, EncodeAcknowledgedBinding(acknowledged)),
            RootMemberRecord.BundleAcknowledgedBinding bundle => (4, EncodeBundleAcknowledgedBinding(bundle)),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown membership binding variant.")
        };

    private static byte[] EncodeProspectiveBinding(RootMemberRecord.ProspectiveBinding value)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalFile(value.VerifiedParentIdentity));
            writer.Field(2, EncodeNameSemantics(value.NameSemantics));
            writer.Field(3, EncodeString(value.RequestedBasename));
        });

    private static byte[] EncodeExistingUnprotectedBinding(RootMemberRecord.ExistingUnprotectedBinding value)
        => Record(writer =>
        {
            writer.Field(1, EncodeStateSlot(value.StateSlot));
            writer.Field(2, EncodePhysicalFile(value.ObservedStateFileIdentity));
            writer.Field(3, DecodeDigest(value.StateBodyDigest));
            writer.Field(4, EncodeBoolean(value.ProtectionMetadataAbsent));
        });

    private static byte[] EncodeAcknowledgedBinding(RootMemberRecord.AcknowledgedBinding value)
        => Record(writer =>
        {
            writer.Field(1, EncodeStateSlot(value.StateSlot));
            writer.Field(2, EncodePhysicalFile(value.ObservedStateFileIdentity));
            writer.Field(3, EncodeProtectionRecord(value.ProtectionRecord));
        });

    private static byte[] EncodeBundleAcknowledgedBinding(RootMemberRecord.BundleAcknowledgedBinding value)
        => Record(writer =>
        {
            writer.Field(1, EncodeStateSlot(value.StateSlot));
            writer.Field(2, EncodePhysicalFile(value.ObservedStateFileIdentity));
            writer.Field(3, GuidBytes(value.LogicalMemberId));
            writer.Field(4, DecodeDigest(value.ParticipantSetDigest));
            writer.Field(5, GuidBytes(value.PublicationId));
            writer.Field(6, EncodeInt64(value.StateGeneration));
            writer.Field(7, DecodeDigest(value.StateBodyDigest));
            writer.Field(8, DecodeDigest(value.BundleDigest));
            writer.Field(9, EncodeProtectionBundleRootRow(value.RootRow));
        });

    private static byte[] EncodeProtectionBundleRootRow(PackageProtectionBundleRootRow row)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(row.RootIdentity));
            writer.Field(2, EncodeInt64(row.EnrollmentEpoch));
            writer.Field(3, EncodeString(row.MemberId));
            writer.Field(4, EncodeInt64(row.Revision));
            writer.Field(5, EncodeInt64(row.StateGeneration));
            writer.Field(6, DecodeDigest(row.StateBodyDigest));
            writer.Field(7, EncodeProtectionClosureV2(row.ActiveClosure));
            writer.Field(8, EncodeProtectionClosureV2(row.RecoverableClosure));
            writer.Field(9, EncodeSequence(row.RetiredGraphs
                .OrderBy(static item => GuidBytes(item.SnapshotId), ByteArrayComparer.Instance), EncodeRetiredGraph));
            writer.Field(10, EncodeBoolean(row.LegacyUnknownRecovery));
            writer.Field(11, DecodeDigest(row.ProtectionDigest));
        });

    private static byte[] EncodeGroupPublicationDescriptorV2(GroupPublicationDescriptorV2 descriptor)
        => Record(writer =>
        {
            writer.Field(1, GuidBytes(descriptor.TransactionId));
            writer.Field(2, GuidBytes(descriptor.LogicalMemberId));
            writer.Field(3, EncodeStateSlot(descriptor.SharedStateSlot));
            writer.Field(4, EncodeInt64(descriptor.PriorStateGeneration));
            writer.Field(5, DecodeDigest(descriptor.PriorStateBodyDigest));
            writer.Field(6, DecodeDigest(descriptor.PriorBundleDigest));
            writer.Field(7, EncodeInt64(descriptor.NextStateGeneration));
            writer.Field(8, DecodeDigest(descriptor.NextStateBodyDigest));
            writer.Field(9, DecodeDigest(descriptor.NextBundleDigest));
            writer.Field(10, DecodeDigest(descriptor.ParticipantSetDigest));
            writer.Field(11, EncodeSequence(descriptor.Participants, EncodeGroupPublicationParticipantV2));
        });

    private static byte[] EncodeGroupPublicationParticipantV2(GroupPublicationParticipantV2 participant)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(participant.RootIdentity));
            writer.Field(2, EncodeInt64(participant.EnrollmentEpoch));
            writer.Field(3, EncodeString(participant.PriorMember.MemberId));
            writer.Field(4, EncodeString(participant.PriorMember.ConfiguredLocator));
            writer.Field(5, EncodeBindingRecord(participant.PriorMember.Binding));
            writer.Field(6, EncodeEnum(participant.PriorMembershipStatus));
            writer.Field(7, EncodeInt32(participant.PriorSchemaVersion));
            writer.Field(8, DecodeDigest(participant.PriorLedgerDigest));
            writer.Field(9, EncodeInt64(participant.PriorRevision));
            writer.Field(10, EncodeNullableRecord(participant.PriorRow, EncodeProtectionBundleRootRow));
            writer.Field(11, EncodeNullableRecord(participant.PriorStateFileIdentity, EncodePhysicalFile));
            writer.Field(12, EncodeInt64(participant.NextRevision));
            writer.Field(13, DecodeDigest(participant.NextRowDigest));
            writer.Field(14, EncodeString(participant.StagedName));
            writer.Field(15, EncodeNullableString(participant.BackupName));
        });

    private static byte[] EncodePendingGroupPublicationV2(PendingGroupPublicationV2 pending)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(pending.RootIdentity));
            writer.Field(2, EncodeGroupPublicationDescriptorV2(pending.Descriptor));
            writer.Field(3, EncodeEnum(pending.Phase));
            writer.Field(4, EncodeNullableRecord(pending.StagedStateFileIdentity, EncodePhysicalFile));
            writer.Field(5, EncodeNullableRecord(pending.BackupStateFileIdentity, EncodePhysicalFile));
            writer.Field(6, EncodeNullableString(pending.BoundCommitDigest));
            writer.Field(7, EncodeEnum(pending.Resolution));
            writer.Field(8, EncodeNullableString(pending.ResolutionDigest));
        });

    private static byte[] EncodeRetiredMember(RootMemberRetirementEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.MemberId));
            writer.Field(2, EncodeAcknowledgedBinding(value.PriorBinding));
            writer.Field(3, EncodeInt64(value.RetiringEpoch));
            writer.Field(4, DecodeDigest(value.ProofDigest));
        });
    }

    private static byte[] EncodePendingCommit(PendingStateCommit value)
        => Record(writer =>
        {
            writer.Field(1, EncodePhysicalRoot(value.RootIdentity));
            writer.Field(2, EncodeInt64(value.EnrollmentEpoch));
            writer.Field(3, EncodeString(value.MemberId));
            writer.Field(4, EncodeBindingRecord(value.Prior));
            writer.Field(5, EncodeProtectionRecord(value.NextProtectionRecord));
            writer.Field(6, EncodeEnum(value.PriorMembershipStatus));
            writer.Field(7, DecodeDigest(value.PriorLedgerDigest));
            writer.Field(8, GuidBytes(value.PublicationId));
            writer.Field(9, EncodeNullableRecord(value.StagedStateFileIdentity, EncodePhysicalFile));
            writer.Field(10, EncodeNullableRecord(value.BackupStateFileIdentity, EncodePhysicalFile));
            writer.Field(11, EncodeEnum(value.Resolution));
        });

    private static byte[] EncodeBindingRecord(RootMemberRecord.MemberBinding value)
    {
        var (tag, payload) = EncodeBinding(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeInt32(tag));
            writer.Field(2, payload);
        });
    }

    private static byte[] EncodeProtectionRecord(PackageProtectionRecord value)
        => Record(writer => EncodeProtectionFields(writer, value, includeStoredDigest: true));

    // The same field tags apply to standalone protection and nested ledger protection.
    // Only the standalone hash omits tag 11 (its own stored digest).
    private static void EncodeProtectionFields(CanonicalWriter writer, PackageProtectionRecord value, bool includeStoredDigest)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.Field(1, EncodeInt32(value.SchemaVersion));
        writer.Field(2, EncodePhysicalRoot(value.RootIdentity));
        writer.Field(3, EncodeInt64(value.EnrollmentEpoch));
        writer.Field(4, EncodeString(value.MemberId));
        writer.Field(5, EncodeInt64(value.Revision));
        writer.Field(6, DecodeDigest(value.StateBodyDigest));
        writer.Field(7, EncodeClosure(value.ActiveClosure));
        writer.Field(8, EncodeClosure(value.RecoverableClosure));
        writer.Field(9, EncodeSequence(value.RetiredGraphs
            .OrderBy(static item => GuidBytes(item.SnapshotId), ByteArrayComparer.Instance), EncodeRetiredGraph));
        writer.Field(10, EncodeBoolean(value.LegacyUnknownRecovery));
        if (includeStoredDigest)
            writer.Field(11, DecodeDigest(value.ProtectionDigest));
    }

    private static byte[] EncodePhysicalRoot(PhysicalRootIdentity value)
        => EncodePhysicalFile(value.HandleIdentity);

    private static byte[] EncodePhysicalFile(PhysicalFileIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.Provider));
            writer.Field(2, EncodeString(value.VolumeOrDeviceId));
            writer.Field(3, EncodeString(value.FileId));
        });
    }

    private static byte[] EncodeStateSlot(StateSlotIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodePhysicalFile(value.ParentIdentity));
            writer.Field(2, EncodeNameSemantics(value.NameSemantics));
            writer.Field(3, EncodeString(value.CanonicalBasename));
        });
    }

    private static byte[] EncodeNameSemantics(PhysicalStoreNameSemantics value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Record(writer =>
        {
            writer.Field(1, EncodeString(value.ProfileId));
            writer.Field(2, EncodeEnum(value.Encoding));
            writer.Field(3, EncodeBoolean(value.CaseSensitive));
            writer.Field(4, EncodeBoolean(value.NormalizationInsensitive));
        });
    }

    private static byte[] Record(Action<CanonicalWriter> encodeFields)
    {
        using var writer = new CanonicalWriter();
        encodeFields(writer);
        return writer.ToArray();
    }

    private sealed class CanonicalWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        internal void Byte(byte value) => _stream.WriteByte(value);

        internal void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

        internal void UInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        internal void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        internal void Count(int value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            UInt32(checked((uint)value));
        }

        internal void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        internal void String(string value)
        {
            var bytes = EncodeString(value);
            LengthPrefixed(bytes);
        }

        internal void LengthPrefixed(ReadOnlySpan<byte> value)
        {
            UInt32(checked((uint)value.Length));
            Bytes(value);
        }

        internal void Field(ushort tag, byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            UInt16(tag);
            LengthPrefixed(payload);
        }

        internal byte[] ToArray() => _stream.ToArray();

        public void Dispose() => _stream.Dispose();
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
