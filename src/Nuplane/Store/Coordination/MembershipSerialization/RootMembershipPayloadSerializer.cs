using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.Coordination.MembershipSerialization;

/// <summary>Strictly serializes descriptive root-membership candidates with bounded payload size.</summary>
/// <remarks>The returned records remain candidates; deserialization does not grant filesystem authority.</remarks>
internal sealed class RootMembershipPayloadSerializer
{
    internal const int MaximumPayloadBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
        Converters = { new PackageProtectionRecordJsonConverter() }
    };

    internal byte[] Serialize(RootMembershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIntegrity(record);

        using var payload = new BoundedControlPayloadStream(MaximumPayloadBytes);
        try { JsonSerializer.Serialize(payload, MembershipDto.FromRecord(record), JsonOptions); }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The root-membership payload exceeds the supported size limit.", exception);
        }
        return payload.ToArray();
    }

    internal RootMembershipRecord Deserialize(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new JsonException("The root-membership payload is empty or exceeds the supported size limit.");

        try
        {
            using var document = JsonDocument.Parse(payload, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A root-membership payload must be a JSON object.");
            PersistedJsonChecks.EnsureUniqueProperties(document.RootElement, "root-membership");
            ValidateBindingShapes(document.RootElement);

            var dto = document.RootElement.Deserialize<MembershipDto>(JsonOptions)
                ?? throw new JsonException("The root-membership payload cannot be null.");
            var record = dto.ToRecord();
            ValidateIntegrity(record);
            return record;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException("The root-membership payload is malformed or inconsistent.", exception);
        }
    }

    private static void ValidateIntegrity(RootMembershipRecord record)
    {
        ValidateProtection(record.Members.SelectMany(static member => member.Binding switch
        {
            RootMemberRecord.AcknowledgedBinding acknowledged => new[] { acknowledged.ProtectionRecord },
            _ => Array.Empty<PackageProtectionRecord>()
        }).Concat(record.RetiredMembers.Select(static evidence => evidence.PriorBinding.ProtectionRecord))
            .Concat(record.PendingStateCommit is { } pending
                ? new[] { pending.NextProtectionRecord }
                : Array.Empty<PackageProtectionRecord>()));

        foreach (var row in record.Members.Select(static member => member.Binding)
                     .OfType<RootMemberRecord.BundleAcknowledgedBinding>().Select(static binding => binding.RootRow)
                     .Concat(record.PendingGroupPublicationV2?.Descriptor.Participants
                         .Where(static participant => participant.PriorRow is not null)
                         .Select(static participant => participant.PriorRow!) ?? []))
        {
            ProtectionDigest.ValidateCanonicalDigest(row.ProtectionDigest);
            if (!string.Equals(ProtectionDigest.PackageProtectionBundleRow(row), row.ProtectionDigest, StringComparison.Ordinal))
                throw new JsonException("A nested package-protection bundle row digest does not match its payload.");
        }

        foreach (var evidence in record.RetiredMembers)
            ProtectionDigest.ValidateCanonicalDigest(evidence.ProofDigest);

        ProtectionDigest.ValidateCanonicalDigest(record.LedgerDigest);
        var actualDigest = ProtectionDigest.Ledger(record);
        if (!string.Equals(record.LedgerDigest, actualDigest, StringComparison.Ordinal))
            throw new JsonException("The root-membership ledger digest does not match its payload.");
    }

    private static void ValidateProtection(IEnumerable<PackageProtectionRecord> records)
    {
        foreach (var record in records)
        {
            ProtectionDigest.ValidateCanonicalDigest(record.ProtectionDigest);
            var actualDigest = ProtectionDigest.Protection(record);
            if (!string.Equals(record.ProtectionDigest, actualDigest, StringComparison.Ordinal))
                throw new JsonException("A nested package-protection digest does not match its payload.");
        }
    }

    private static void ValidateBindingShapes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (element.TryGetProperty("tag", out var tagElement))
                {
                    if (!tagElement.TryGetInt32(out var tag))
                        throw new JsonException("A member binding tag must be an integer.");

                    var expected = tag switch
                    {
                        0 => new[] { "tag" },
                        1 => new[] { "tag", "verifiedParentIdentity", "nameSemantics", "requestedBasename" },
                        2 => new[] { "tag", "stateSlot", "observedStateFileIdentity", "stateBodyDigest", "protectionMetadataAbsent" },
                        3 => new[] { "tag", "stateSlot", "observedStateFileIdentity", "protectionRecord" },
                        4 => new[] { "tag", "stateSlot", "observedStateFileIdentity", "logicalMemberId", "participantSetDigest", "publicationId", "stateGeneration", "stateBodyDigest", "bundleDigest", "rootRow" },
                        _ => throw new JsonException("A member binding tag is unsupported.")
                    };
                    var actual = element.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
                    if (!actual.SetEquals(expected))
                        throw new JsonException("A member binding contains fields that do not match its tag.");
                }

                foreach (var property in element.EnumerateObject())
                    ValidateBindingShapes(property.Value);
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateBindingShapes(item);
                break;
        }
    }

    private static int Required(int? value, string name)
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static long Required(long? value, string name)
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static bool Required(bool? value, string name)
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static Guid Required(Guid? value, string name)
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static string Required(string? value, string name)
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static T Required<T>(T? value, string name) where T : class
        => value ?? throw new JsonException($"The root-membership payload is missing '{name}'.");

    private static List<T> RequiredItems<T>(List<T?>? values, string name) where T : class
    {
        if (values is null)
            throw new JsonException($"The root-membership payload is missing '{name}'.");
        var result = new List<T>(values.Count);
        foreach (var value in values)
            result.Add(value ?? throw new JsonException($"The root-membership payload '{name}' cannot contain null items."));
        return result;
    }

    private static TEnum ReadEnum<TEnum>(int? value, string name) where TEnum : struct, Enum
    {
        var raw = Required(value, name);
        var result = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return Enum.IsDefined(result) ? result : throw new JsonException($"The root-membership payload has unsupported '{name}'.");
    }

    private sealed class MembershipDto
    {
        public MembershipDto() { }

        public int? SchemaVersion { get; set; }
        public PhysicalRootDto? RootIdentity { get; set; }
        public long? EnrollmentEpoch { get; set; }
        public int? Status { get; set; }
        public List<MemberDto?>? Members { get; set; }
        public List<string?>? TargetMemberIds { get; set; }
        public List<RetiredMemberDto?>? RetiredMembers { get; set; }
        public PendingCommitDto? PendingStateCommit { get; set; }
        public PendingGroupPublicationDto? PendingGroupPublicationV2 { get; set; }
        public string? LedgerDigest { get; set; }

        internal RootMembershipRecord ToRecord()
        {
            var members = RequiredItems(Members, nameof(Members)).Select(static member => member.ToRecord()).ToArray();
            var targets = TargetMemberIds ?? throw new JsonException("The root-membership payload is missing 'targetMemberIds'.");
            if (targets.Any(static value => value is null))
                throw new JsonException("The root-membership payload target identifiers cannot contain null items.");

            var retired = RequiredItems(RetiredMembers, nameof(RetiredMembers)).Select(static item => item.ToRecord()).ToArray();
            var root = Required(RootIdentity, nameof(RootIdentity)).ToRecord();
            var status = ReadEnum<RootMembershipStatus>(Status, nameof(Status));
            var pending = PendingStateCommit?.ToRecord(members);
            var groupPending = PendingGroupPublicationV2?.ToRecord();
            return new RootMembershipRecord(
                Required(SchemaVersion, nameof(SchemaVersion)),
                root,
                Required(EnrollmentEpoch, nameof(EnrollmentEpoch)),
                status,
                members,
                targets.Select(static value => value!).ToArray(),
                retired,
                pending,
                Required(LedgerDigest, nameof(LedgerDigest)),
                groupPending);
        }

        internal static MembershipDto FromRecord(RootMembershipRecord record)
            => new()
            {
                SchemaVersion = record.SchemaVersion,
                RootIdentity = PhysicalRootDto.FromRecord(record.RootIdentity),
                EnrollmentEpoch = record.EnrollmentEpoch,
                Status = (int)record.Status,
                Members = record.Members.Select(static member => (MemberDto?)MemberDto.FromRecord(member)).ToList(),
                TargetMemberIds = record.TargetMemberIds.Cast<string?>().ToList(),
                RetiredMembers = record.RetiredMembers.Select(static retired => (RetiredMemberDto?)RetiredMemberDto.FromRecord(retired)).ToList(),
                PendingStateCommit = record.PendingStateCommit is null ? null : PendingCommitDto.FromRecord(record.PendingStateCommit),
                PendingGroupPublicationV2 = record.PendingGroupPublicationV2 is null ? null : PendingGroupPublicationDto.FromRecord(record.PendingGroupPublicationV2),
                LedgerDigest = record.LedgerDigest
            };
    }

    private sealed class MemberDto
    {
        public MemberDto() { }

        public string? MemberId { get; set; }
        public string? ConfiguredLocator { get; set; }
        public BindingDto? Binding { get; set; }

        internal RootMemberRecord ToRecord()
            => new(Required(MemberId, nameof(MemberId)), Required(ConfiguredLocator, nameof(ConfiguredLocator)),
                Required(Binding, nameof(Binding)).ToRecord());

        internal static MemberDto FromRecord(RootMemberRecord value)
            => new() { MemberId = value.MemberId, ConfiguredLocator = value.ConfiguredLocator, Binding = BindingDto.FromRecord(value.Binding) };
    }

    private sealed class BindingDto
    {
        public BindingDto() { }

        public int? Tag { get; set; }
        public PhysicalFileDto? VerifiedParentIdentity { get; set; }
        public NameSemanticsDto? NameSemantics { get; set; }
        public string? RequestedBasename { get; set; }
        public StateSlotDto? StateSlot { get; set; }
        public PhysicalFileDto? ObservedStateFileIdentity { get; set; }
        public string? StateBodyDigest { get; set; }
        public bool? ProtectionMetadataAbsent { get; set; }
        public PackageProtectionRecord? ProtectionRecord { get; set; }
        public Guid? LogicalMemberId { get; set; }
        public string? ParticipantSetDigest { get; set; }
        public Guid? PublicationId { get; set; }
        public long? StateGeneration { get; set; }
        public string? BundleDigest { get; set; }
        public PackageProtectionBundleRootRowDto? RootRow { get; set; }

        internal RootMemberRecord.MemberBinding ToRecord()
            => Required(Tag, nameof(Tag)) switch
            {
                0 => new RootMemberRecord.DeclaredBinding(),
                1 => new RootMemberRecord.ProspectiveBinding(
                    Required(VerifiedParentIdentity, nameof(VerifiedParentIdentity)).ToRecord(),
                    Required(NameSemantics, nameof(NameSemantics)).ToRecord(),
                    Required(RequestedBasename, nameof(RequestedBasename))),
                2 => new RootMemberRecord.ExistingUnprotectedBinding(
                    Required(StateSlot, nameof(StateSlot)).ToRecord(),
                    Required(ObservedStateFileIdentity, nameof(ObservedStateFileIdentity)).ToRecord(),
                    Required(StateBodyDigest, nameof(StateBodyDigest)),
                    Required(ProtectionMetadataAbsent, nameof(ProtectionMetadataAbsent))),
                3 => new RootMemberRecord.AcknowledgedBinding(
                    Required(StateSlot, nameof(StateSlot)).ToRecord(),
                    Required(ObservedStateFileIdentity, nameof(ObservedStateFileIdentity)).ToRecord(),
                    Required(ProtectionRecord, nameof(ProtectionRecord))),
                4 => new RootMemberRecord.BundleAcknowledgedBinding(
                    Required(StateSlot, nameof(StateSlot)).ToRecord(),
                    Required(ObservedStateFileIdentity, nameof(ObservedStateFileIdentity)).ToRecord(),
                    Required(LogicalMemberId, nameof(LogicalMemberId)),
                    Required(ParticipantSetDigest, nameof(ParticipantSetDigest)),
                    Required(PublicationId, nameof(PublicationId)),
                    Required(StateGeneration, nameof(StateGeneration)),
                    Required(StateBodyDigest, nameof(StateBodyDigest)),
                    Required(BundleDigest, nameof(BundleDigest)),
                    Required(RootRow, nameof(RootRow)).ToRow()),
                _ => throw new JsonException("A member binding tag is unsupported.")
            };

        internal static BindingDto FromRecord(RootMemberRecord.MemberBinding value)
            => value switch
            {
                RootMemberRecord.DeclaredBinding => new() { Tag = 0 },
                RootMemberRecord.ProspectiveBinding prospective => new()
                {
                    Tag = 1,
                    VerifiedParentIdentity = PhysicalFileDto.FromRecord(prospective.VerifiedParentIdentity),
                    NameSemantics = NameSemanticsDto.FromRecord(prospective.NameSemantics),
                    RequestedBasename = prospective.RequestedBasename
                },
                RootMemberRecord.ExistingUnprotectedBinding unprotected => new()
                {
                    Tag = 2,
                    StateSlot = StateSlotDto.FromRecord(unprotected.StateSlot),
                    ObservedStateFileIdentity = PhysicalFileDto.FromRecord(unprotected.ObservedStateFileIdentity),
                    StateBodyDigest = unprotected.StateBodyDigest,
                    ProtectionMetadataAbsent = unprotected.ProtectionMetadataAbsent
                },
                RootMemberRecord.AcknowledgedBinding acknowledged => new()
                {
                    Tag = 3,
                    StateSlot = StateSlotDto.FromRecord(acknowledged.StateSlot),
                    ObservedStateFileIdentity = PhysicalFileDto.FromRecord(acknowledged.ObservedStateFileIdentity),
                    ProtectionRecord = acknowledged.ProtectionRecord
                },
                RootMemberRecord.BundleAcknowledgedBinding bundle => new()
                {
                    Tag = 4,
                    StateSlot = StateSlotDto.FromRecord(bundle.StateSlot),
                    ObservedStateFileIdentity = PhysicalFileDto.FromRecord(bundle.ObservedStateFileIdentity),
                    LogicalMemberId = bundle.LogicalMemberId,
                    ParticipantSetDigest = bundle.ParticipantSetDigest,
                    PublicationId = bundle.PublicationId,
                    StateGeneration = bundle.StateGeneration,
                    StateBodyDigest = bundle.StateBodyDigest,
                    BundleDigest = bundle.BundleDigest,
                    RootRow = PackageProtectionBundleRootRowDto.FromRow(bundle.RootRow)
                },
                _ => throw new InvalidOperationException("The membership candidate has an unsupported binding type.")
            };
    }

    private sealed class PendingCommitDto
    {
        public PendingCommitDto() { }

        public PhysicalRootDto? RootIdentity { get; set; }
        public long? EnrollmentEpoch { get; set; }
        public int? PriorMembershipStatus { get; set; }
        public string? PriorLedgerDigest { get; set; }
        public Guid? PublicationId { get; set; }
        public string? MemberId { get; set; }
        public BindingDto? Prior { get; set; }
        public PackageProtectionRecord? NextProtectionRecord { get; set; }
        public PhysicalFileDto? StagedStateFileIdentity { get; set; }
        public PhysicalFileDto? BackupStateFileIdentity { get; set; }
        public int? Resolution { get; set; }

        internal PendingStateCommit ToRecord(IReadOnlyList<RootMemberRecord> members)
        {
            var memberId = Required(MemberId, nameof(MemberId));
            var member = members.SingleOrDefault(candidate => string.Equals(candidate.MemberId, memberId, StringComparison.Ordinal))
                ?? throw new JsonException("A pending state commit references an unknown member.");
            var prior = Required(Prior, nameof(Prior)).ToRecord();
            var priorMember = new RootMemberRecord(member.MemberId, member.ConfiguredLocator, prior);
            return new PendingStateCommit(
                Required(RootIdentity, nameof(RootIdentity)).ToRecord(),
                Required(EnrollmentEpoch, nameof(EnrollmentEpoch)),
                ReadEnum<RootMembershipStatus>(PriorMembershipStatus, nameof(PriorMembershipStatus)),
                Required(PriorLedgerDigest, nameof(PriorLedgerDigest)),
                Required(PublicationId, nameof(PublicationId)),
                priorMember,
                Required(NextProtectionRecord, nameof(NextProtectionRecord)),
                StagedStateFileIdentity?.ToRecord(),
                BackupStateFileIdentity?.ToRecord(),
                ReadEnum<PendingStateCommitResolution>(Resolution, nameof(Resolution)));
        }

        internal static PendingCommitDto FromRecord(PendingStateCommit value)
            => new()
            {
                RootIdentity = PhysicalRootDto.FromRecord(value.RootIdentity),
                EnrollmentEpoch = value.EnrollmentEpoch,
                PriorMembershipStatus = (int)value.PriorMembershipStatus,
                PriorLedgerDigest = value.PriorLedgerDigest,
                PublicationId = value.PublicationId,
                MemberId = value.MemberId,
                Prior = BindingDto.FromRecord(value.Prior),
                NextProtectionRecord = value.NextProtectionRecord,
                StagedStateFileIdentity = value.StagedStateFileIdentity is null ? null : PhysicalFileDto.FromRecord(value.StagedStateFileIdentity),
                BackupStateFileIdentity = value.BackupStateFileIdentity is null ? null : PhysicalFileDto.FromRecord(value.BackupStateFileIdentity),
                Resolution = (int)value.Resolution
        };
    }

    private sealed class PendingGroupPublicationDto
    {
        public PhysicalRootDto? RootIdentity { get; set; }
        public GroupPublicationDescriptorDto? Descriptor { get; set; }
        public int? Phase { get; set; }
        public PhysicalFileDto? StagedStateFileIdentity { get; set; }
        public PhysicalFileDto? BackupStateFileIdentity { get; set; }
        public string? BoundCommitDigest { get; set; }
        public int? Resolution { get; set; }
        public string? ResolutionDigest { get; set; }

        internal PendingGroupPublicationV2 ToRecord()
            => new(
                Required(Descriptor, nameof(Descriptor)).ToRecord(),
                Required(RootIdentity, nameof(RootIdentity)).ToRecord(),
                ReadEnum<GroupPublicationPhaseV2>(Phase, nameof(Phase)),
                StagedStateFileIdentity?.ToRecord(),
                BackupStateFileIdentity?.ToRecord(),
                ReadEnum<GroupPublicationResolutionV2>(Resolution, nameof(Resolution)),
                BoundCommitDigest,
                ResolutionDigest);

        internal static PendingGroupPublicationDto FromRecord(PendingGroupPublicationV2 value)
            => new()
            {
                RootIdentity = PhysicalRootDto.FromRecord(value.RootIdentity),
                Descriptor = GroupPublicationDescriptorDto.FromRecord(value.Descriptor),
                Phase = (int)value.Phase,
                StagedStateFileIdentity = value.StagedStateFileIdentity is null ? null : PhysicalFileDto.FromRecord(value.StagedStateFileIdentity),
                BackupStateFileIdentity = value.BackupStateFileIdentity is null ? null : PhysicalFileDto.FromRecord(value.BackupStateFileIdentity),
                BoundCommitDigest = value.BoundCommitDigest,
                Resolution = (int)value.Resolution,
                ResolutionDigest = value.ResolutionDigest
            };
    }

    private sealed class GroupPublicationDescriptorDto
    {
        public Guid? TransactionId { get; set; }
        public Guid? LogicalMemberId { get; set; }
        public StateSlotDto? SharedStateSlot { get; set; }
        public long? PriorStateGeneration { get; set; }
        public string? PriorStateBodyDigest { get; set; }
        public string? PriorBundleDigest { get; set; }
        public long? NextStateGeneration { get; set; }
        public string? NextStateBodyDigest { get; set; }
        public string? NextBundleDigest { get; set; }
        public string? ParticipantSetDigest { get; set; }
        public List<GroupPublicationParticipantDto?>? Participants { get; set; }
        public string? IntentDigest { get; set; }

        internal GroupPublicationDescriptorV2 ToRecord()
            => new(
                Required(TransactionId, nameof(TransactionId)),
                Required(LogicalMemberId, nameof(LogicalMemberId)),
                Required(SharedStateSlot, nameof(SharedStateSlot)).ToRecord(),
                Required(PriorStateGeneration, nameof(PriorStateGeneration)),
                Required(PriorStateBodyDigest, nameof(PriorStateBodyDigest)),
                Required(PriorBundleDigest, nameof(PriorBundleDigest)),
                Required(NextStateGeneration, nameof(NextStateGeneration)),
                Required(NextStateBodyDigest, nameof(NextStateBodyDigest)),
                Required(NextBundleDigest, nameof(NextBundleDigest)),
                RequiredItems(Participants, nameof(Participants)).Select(static participant => participant.ToRecord()).ToArray(),
                Required(ParticipantSetDigest, nameof(ParticipantSetDigest)),
                Required(IntentDigest, nameof(IntentDigest)));

        internal static GroupPublicationDescriptorDto FromRecord(GroupPublicationDescriptorV2 value)
            => new()
            {
                TransactionId = value.TransactionId,
                LogicalMemberId = value.LogicalMemberId,
                SharedStateSlot = StateSlotDto.FromRecord(value.SharedStateSlot),
                PriorStateGeneration = value.PriorStateGeneration,
                PriorStateBodyDigest = value.PriorStateBodyDigest,
                PriorBundleDigest = value.PriorBundleDigest,
                NextStateGeneration = value.NextStateGeneration,
                NextStateBodyDigest = value.NextStateBodyDigest,
                NextBundleDigest = value.NextBundleDigest,
                ParticipantSetDigest = value.ParticipantSetDigest,
                Participants = value.Participants.Select(static participant => (GroupPublicationParticipantDto?)GroupPublicationParticipantDto.FromRecord(participant)).ToList(),
                IntentDigest = value.IntentDigest
            };
    }

    private sealed class GroupPublicationParticipantDto
    {
        public PhysicalRootDto? RootIdentity { get; set; }
        public long? EnrollmentEpoch { get; set; }
        public string? PriorMemberId { get; set; }
        public string? PriorConfiguredLocator { get; set; }
        public BindingDto? PriorBinding { get; set; }
        public int? PriorMembershipStatus { get; set; }
        public int? PriorSchemaVersion { get; set; }
        public string? PriorLedgerDigest { get; set; }
        public long? PriorRevision { get; set; }
        public PackageProtectionBundleRootRowDto? PriorRow { get; set; }
        public PhysicalFileDto? PriorStateFileIdentity { get; set; }
        public long? NextRevision { get; set; }
        public string? NextRowDigest { get; set; }
        public string? StagedName { get; set; }
        public string? BackupName { get; set; }

        internal GroupPublicationParticipantV2 ToRecord()
        {
            var priorMember = new RootMemberRecord(
                Required(PriorMemberId, nameof(PriorMemberId)),
                Required(PriorConfiguredLocator, nameof(PriorConfiguredLocator)),
                Required(PriorBinding, nameof(PriorBinding)).ToRecord());
            return new GroupPublicationParticipantV2(
                Required(RootIdentity, nameof(RootIdentity)).ToRecord(),
                Required(EnrollmentEpoch, nameof(EnrollmentEpoch)),
                priorMember,
                ReadEnum<RootMembershipStatus>(PriorMembershipStatus, nameof(PriorMembershipStatus)),
                Required(PriorSchemaVersion, nameof(PriorSchemaVersion)),
                Required(PriorLedgerDigest, nameof(PriorLedgerDigest)),
                Required(PriorRevision, nameof(PriorRevision)),
                PriorRow?.ToRow(),
                PriorStateFileIdentity?.ToRecord(),
                Required(NextRevision, nameof(NextRevision)),
                Required(NextRowDigest, nameof(NextRowDigest)),
                Required(StagedName, nameof(StagedName)),
                BackupName);
        }

        internal static GroupPublicationParticipantDto FromRecord(GroupPublicationParticipantV2 value)
            => new()
            {
                RootIdentity = PhysicalRootDto.FromRecord(value.RootIdentity),
                EnrollmentEpoch = value.EnrollmentEpoch,
                PriorMemberId = value.PriorMember.MemberId,
                PriorConfiguredLocator = value.PriorMember.ConfiguredLocator,
                PriorBinding = BindingDto.FromRecord(value.PriorMember.Binding),
                PriorMembershipStatus = (int)value.PriorMembershipStatus,
                PriorSchemaVersion = value.PriorSchemaVersion,
                PriorLedgerDigest = value.PriorLedgerDigest,
                PriorRevision = value.PriorRevision,
                PriorRow = value.PriorRow is null ? null : PackageProtectionBundleRootRowDto.FromRow(value.PriorRow),
                PriorStateFileIdentity = value.PriorStateFileIdentity is null ? null : PhysicalFileDto.FromRecord(value.PriorStateFileIdentity),
                NextRevision = value.NextRevision,
                NextRowDigest = value.NextRowDigest,
                StagedName = value.StagedName,
                BackupName = value.BackupName
            };
    }

    private sealed class RetiredMemberDto
    {
        public RetiredMemberDto() { }

        public string? MemberId { get; set; }
        public AcknowledgedBindingDto? PriorBinding { get; set; }
        public long? RetiringEpoch { get; set; }
        public string? ProofDigest { get; set; }

        internal RootMemberRetirementEvidence ToRecord()
        {
            var proof = Required(ProofDigest, nameof(ProofDigest));
            ProtectionDigest.ValidateCanonicalDigest(proof);
            return new RootMemberRetirementEvidence(
                Required(MemberId, nameof(MemberId)),
                Required(PriorBinding, nameof(PriorBinding)).ToRecord(),
                Required(RetiringEpoch, nameof(RetiringEpoch)),
                proof);
        }

        internal static RetiredMemberDto FromRecord(RootMemberRetirementEvidence value)
            => new()
            {
                MemberId = value.MemberId,
                PriorBinding = AcknowledgedBindingDto.FromRecord(value.PriorBinding),
                RetiringEpoch = value.RetiringEpoch,
                ProofDigest = value.ProofDigest
            };
    }

    private sealed class AcknowledgedBindingDto
    {
        public AcknowledgedBindingDto() { }

        public StateSlotDto? StateSlot { get; set; }
        public PhysicalFileDto? ObservedStateFileIdentity { get; set; }
        public PackageProtectionRecord? ProtectionRecord { get; set; }

        internal RootMemberRecord.AcknowledgedBinding ToRecord()
            => new(Required(StateSlot, nameof(StateSlot)).ToRecord(),
                Required(ObservedStateFileIdentity, nameof(ObservedStateFileIdentity)).ToRecord(),
                Required(ProtectionRecord, nameof(ProtectionRecord)));

        internal static AcknowledgedBindingDto FromRecord(RootMemberRecord.AcknowledgedBinding value)
            => new()
            {
                StateSlot = StateSlotDto.FromRecord(value.StateSlot),
                ObservedStateFileIdentity = PhysicalFileDto.FromRecord(value.ObservedStateFileIdentity),
                ProtectionRecord = value.ProtectionRecord
            };
    }

    private sealed class PhysicalRootDto
    {
        public PhysicalRootDto() { }

        public PhysicalFileDto? HandleIdentity { get; set; }
        internal PhysicalRootIdentity ToRecord() => new(Required(HandleIdentity, nameof(HandleIdentity)).ToRecord());
        internal static PhysicalRootDto FromRecord(PhysicalRootIdentity value) => new() { HandleIdentity = PhysicalFileDto.FromRecord(value.HandleIdentity) };
    }

    private sealed class PhysicalFileDto
    {
        public PhysicalFileDto() { }

        public string? Provider { get; set; }
        public string? VolumeOrDeviceId { get; set; }
        public string? FileId { get; set; }
        internal PhysicalFileIdentity ToRecord() => new(Required(Provider, nameof(Provider)), Required(VolumeOrDeviceId, nameof(VolumeOrDeviceId)), Required(FileId, nameof(FileId)));
        internal static PhysicalFileDto FromRecord(PhysicalFileIdentity value) => new() { Provider = value.Provider, VolumeOrDeviceId = value.VolumeOrDeviceId, FileId = value.FileId };
    }

    private sealed class StateSlotDto
    {
        public StateSlotDto() { }

        public PhysicalFileDto? ParentIdentity { get; set; }
        public NameSemanticsDto? NameSemantics { get; set; }
        public string? CanonicalBasename { get; set; }
        internal StateSlotIdentity ToRecord() => new(Required(ParentIdentity, nameof(ParentIdentity)).ToRecord(), Required(NameSemantics, nameof(NameSemantics)).ToRecord(), Required(CanonicalBasename, nameof(CanonicalBasename)));
        internal static StateSlotDto FromRecord(StateSlotIdentity value) => new() { ParentIdentity = PhysicalFileDto.FromRecord(value.ParentIdentity), NameSemantics = NameSemanticsDto.FromRecord(value.NameSemantics), CanonicalBasename = value.CanonicalBasename };
    }

    private sealed class NameSemanticsDto
    {
        public NameSemanticsDto() { }

        public string? ProfileId { get; set; }
        public int? Encoding { get; set; }
        public bool? CaseSensitive { get; set; }
        public bool? NormalizationInsensitive { get; set; }
        internal PhysicalStoreNameSemantics ToRecord() => new(
            Required(ProfileId, nameof(ProfileId)),
            ReadEnum<PhysicalStoreNameEncoding>(Encoding, nameof(Encoding)),
            Required(CaseSensitive, nameof(CaseSensitive)),
            Required(NormalizationInsensitive, nameof(NormalizationInsensitive)));
        internal static NameSemanticsDto FromRecord(PhysicalStoreNameSemantics value) => new()
        {
            ProfileId = value.ProfileId,
            Encoding = (int)value.Encoding,
            CaseSensitive = value.CaseSensitive,
            NormalizationInsensitive = value.NormalizationInsensitive
        };
    }
}
