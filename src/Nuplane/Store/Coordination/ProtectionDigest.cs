using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
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

    /// <summary>Hashes every legacy state-body field, excluding the optional protection property.</summary>
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
