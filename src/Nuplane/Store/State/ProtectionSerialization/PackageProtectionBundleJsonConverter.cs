using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.State.ProtectionSerialization;

/// <summary>Reads and writes descriptive v2 protection bundles without exposing their constructors.</summary>
public sealed class PackageProtectionBundleJsonConverter : JsonConverter<PackageProtectionBundle>
{
    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64
    };

    /// <inheritdoc />
    public override PackageProtectionBundle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        PersistedJsonChecks.EnsureUniqueProperties(document.RootElement, "protectionBundle");
        try
        {
            var dto = document.RootElement.Deserialize<PackageProtectionBundleDto>(BundleJsonOptions)
                ?? throw new JsonException("The protection bundle cannot be null.");
            return dto.ToBundle();
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new JsonException("The multiroot protection bundle is malformed or inconsistent.", exception);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PackageProtectionBundle value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var validated = value.Copy();
        JsonSerializer.Serialize(writer, PackageProtectionBundleDto.FromBundle(validated), BundleJsonOptions);
    }
}

internal sealed class PackageProtectionBundleDto
{
    public int? SchemaVersion { get; set; }
    public Guid? LogicalMemberId { get; set; }
    public Guid? PublicationId { get; set; }
    public long? StateGeneration { get; set; }
    public string? StateBodyDigest { get; set; }
    public string? ParticipantSetDigest { get; set; }
    public List<PackageProtectionBundleRootRowDto?>? Rows { get; set; }
    public string? BundleDigest { get; set; }

    internal PackageProtectionBundle ToBundle()
    {
        var rows = ProtectionDtoHelpers.MapRequired(Rows, nameof(Rows), static row => row.ToRow());
        for (var index = 1; index < rows.Count; index++)
        {
            if (PhysicalRootIdentityComparer.Instance.Compare(rows[index - 1].RootIdentity, rows[index].RootIdentity) >= 0)
                throw new JsonException("Protection bundle rows must be unique and sorted by native root identity.");
        }

        return new PackageProtectionBundle(
            SchemaVersion ?? throw ProtectionDtoHelpers.Missing(nameof(SchemaVersion)),
            LogicalMemberId ?? throw ProtectionDtoHelpers.Missing(nameof(LogicalMemberId)),
            PublicationId ?? throw ProtectionDtoHelpers.Missing(nameof(PublicationId)),
            StateGeneration ?? throw ProtectionDtoHelpers.Missing(nameof(StateGeneration)),
            ProtectionDtoHelpers.Required(StateBodyDigest, nameof(StateBodyDigest)),
            rows,
            ProtectionDtoHelpers.Required(ParticipantSetDigest, nameof(ParticipantSetDigest)),
            ProtectionDtoHelpers.Required(BundleDigest, nameof(BundleDigest)));
    }

    internal static PackageProtectionBundleDto FromBundle(PackageProtectionBundle bundle)
        => new()
        {
            SchemaVersion = bundle.SchemaVersion,
            LogicalMemberId = bundle.LogicalMemberId,
            PublicationId = bundle.PublicationId,
            StateGeneration = bundle.StateGeneration,
            StateBodyDigest = bundle.StateBodyDigest,
            ParticipantSetDigest = bundle.ParticipantSetDigest,
            Rows = bundle.Rows.Select(static row => (PackageProtectionBundleRootRowDto?)PackageProtectionBundleRootRowDto.FromRow(row)).ToList(),
            BundleDigest = bundle.BundleDigest
        };
}

internal sealed class PackageProtectionBundleRootRowDto
{
    public PhysicalRootIdentityDto? RootIdentity { get; set; }
    public long? EnrollmentEpoch { get; set; }
    public string? MemberId { get; set; }
    public long? Revision { get; set; }
    public long? StateGeneration { get; set; }
    public string? StateBodyDigest { get; set; }
    public string? ProtectionDigest { get; set; }
    public PackageProtectionClosureV2Dto? ActiveClosure { get; set; }
    public PackageProtectionClosureV2Dto? RecoverableClosure { get; set; }
    public List<RetiredGraphEvidenceDto?>? RetiredGraphs { get; set; }
    public bool? LegacyUnknownRecovery { get; set; }

    internal PackageProtectionBundleRootRow ToRow()
        => new(
            ProtectionDtoHelpers.Required(RootIdentity, nameof(RootIdentity)).ToIdentity(),
            EnrollmentEpoch ?? throw ProtectionDtoHelpers.Missing(nameof(EnrollmentEpoch)),
            ProtectionDtoHelpers.Required(MemberId, nameof(MemberId)),
            Revision ?? throw ProtectionDtoHelpers.Missing(nameof(Revision)),
            StateGeneration ?? throw ProtectionDtoHelpers.Missing(nameof(StateGeneration)),
            ProtectionDtoHelpers.Required(StateBodyDigest, nameof(StateBodyDigest)),
            ProtectionDtoHelpers.Required(ActiveClosure, nameof(ActiveClosure)).ToClosure(),
            ProtectionDtoHelpers.Required(RecoverableClosure, nameof(RecoverableClosure)).ToClosure(),
            ProtectionDtoHelpers.MapRequired(RetiredGraphs, nameof(RetiredGraphs), static item => item.ToEvidence()),
            LegacyUnknownRecovery ?? throw ProtectionDtoHelpers.Missing(nameof(LegacyUnknownRecovery)),
            ProtectionDtoHelpers.Required(ProtectionDigest, nameof(ProtectionDigest)));

    internal static PackageProtectionBundleRootRowDto FromRow(PackageProtectionBundleRootRow row)
        => new()
        {
            RootIdentity = PhysicalRootIdentityDto.FromIdentity(row.RootIdentity),
            EnrollmentEpoch = row.EnrollmentEpoch,
            MemberId = row.MemberId,
            Revision = row.Revision,
            StateGeneration = row.StateGeneration,
            StateBodyDigest = row.StateBodyDigest,
            ProtectionDigest = row.ProtectionDigest,
            ActiveClosure = PackageProtectionClosureV2Dto.FromClosure(row.ActiveClosure),
            RecoverableClosure = PackageProtectionClosureV2Dto.FromClosure(row.RecoverableClosure),
            RetiredGraphs = row.RetiredGraphs.Select(static retired => (RetiredGraphEvidenceDto?)RetiredGraphEvidenceDto.FromEvidence(retired)).ToList(),
            LegacyUnknownRecovery = row.LegacyUnknownRecovery
        };
}

internal sealed class PackageProtectionClosureV2Dto
{
    public int? Knowledge { get; set; }
    public int? UnknownReason { get; set; }
    public List<ProtectedGraphSnapshotV2Dto?>? Graphs { get; set; }

    internal PackageProtectionClosureV2 ToClosure()
    {
        if (Knowledge is null || !Enum.IsDefined((PackageProtectionClosureKnowledge)Knowledge.Value))
            throw new JsonException("The v2 protection closure has an invalid or missing knowledge tag.");
        var knowledge = (PackageProtectionClosureKnowledge)Knowledge.Value;
        PackageProtectionUnknownReasonCode? reason = null;
        if (UnknownReason is not null)
        {
            reason = (PackageProtectionUnknownReasonCode)UnknownReason.Value;
            if (!Enum.IsDefined(reason.Value))
                throw new JsonException("The v2 protection closure has an invalid unknown reason.");
        }

        if (knowledge == PackageProtectionClosureKnowledge.Unknown)
        {
            if (reason is null || Graphs is not null)
                throw new JsonException("An unknown v2 closure requires one reason and no graph collection.");
            return new PackageProtectionClosureV2(knowledge, reason, graphs: null);
        }
        if (reason is not null || Graphs is null)
            throw new JsonException("A known v2 closure requires a graph array and cannot carry an unknown reason.");
        return new PackageProtectionClosureV2(knowledge, unknownReason: null,
            ProtectionDtoHelpers.MapRequired(Graphs, nameof(Graphs), static graph => graph.ToSnapshot()));
    }

    internal static PackageProtectionClosureV2Dto FromClosure(PackageProtectionClosureV2 closure)
        => new()
        {
            Knowledge = (int)closure.Knowledge,
            UnknownReason = closure.UnknownReason is null ? null : (int)closure.UnknownReason.Value,
            Graphs = closure.Graphs?.Select(static graph => (ProtectedGraphSnapshotV2Dto?)ProtectedGraphSnapshotV2Dto.FromSnapshot(graph)).ToList()
        };
}

internal sealed class ProtectedGraphSnapshotV2Dto
{
    public Guid? SnapshotId { get; set; }
    public string? GraphId { get; set; }
    public string? GenerationId { get; set; }
    public int? Disposition { get; set; }
    public List<PhysicalRootIdentityDto?>? Roots { get; set; }
    public List<PackageGraphRootSelectionDto?>? RequestedRoots { get; set; }
    public List<PackageGraphNodeIdentityDto?>? Nodes { get; set; }
    public List<PackageGraphEdgeIdentityDto?>? Edges { get; set; }
    public ProtectedGraphRecoverySelectionEvidenceV2Dto? RecoverySelectionEvidence { get; set; }

    internal ProtectedGraphSnapshotV2 ToSnapshot()
    {
        if (Disposition is null || !Enum.IsDefined((ProtectedGraphDisposition)Disposition.Value))
            throw new JsonException("A v2 graph snapshot has an invalid or missing disposition.");
        return new ProtectedGraphSnapshotV2(
            SnapshotId ?? throw ProtectionDtoHelpers.Missing(nameof(SnapshotId)),
            ProtectionDtoHelpers.Required(GraphId, nameof(GraphId)),
            ProtectionDtoHelpers.Required(GenerationId, nameof(GenerationId)),
            (ProtectedGraphDisposition)Disposition.Value,
            ProtectionDtoHelpers.MapRequired(Roots, nameof(Roots), static item => item.ToIdentity()),
            ProtectionDtoHelpers.MapRequired(RequestedRoots, nameof(RequestedRoots), static item => item.ToSelection()),
            ProtectionDtoHelpers.MapRequired(Nodes, nameof(Nodes), static item => item.ToNode()),
            ProtectionDtoHelpers.MapRequired(Edges, nameof(Edges), static item => item.ToEdge()),
            RecoverySelectionEvidence?.ToEvidence());
    }

    internal static ProtectedGraphSnapshotV2Dto FromSnapshot(ProtectedGraphSnapshotV2 snapshot)
        => new()
        {
            SnapshotId = snapshot.SnapshotId,
            GraphId = snapshot.GraphId,
            GenerationId = snapshot.GenerationId,
            Disposition = (int)snapshot.Disposition,
            Roots = snapshot.Roots.Select(static item => (PhysicalRootIdentityDto?)PhysicalRootIdentityDto.FromIdentity(item)).ToList(),
            RequestedRoots = snapshot.RequestedRoots.Select(static item => (PackageGraphRootSelectionDto?)PackageGraphRootSelectionDto.FromSelection(item)).ToList(),
            Nodes = snapshot.Nodes.Select(static item => (PackageGraphNodeIdentityDto?)PackageGraphNodeIdentityDto.FromNode(item)).ToList(),
            Edges = snapshot.Edges.Select(static item => (PackageGraphEdgeIdentityDto?)PackageGraphEdgeIdentityDto.FromEdge(item)).ToList(),
            RecoverySelectionEvidence = snapshot.RecoverySelectionEvidence is null
                ? null
                : ProtectedGraphRecoverySelectionEvidenceV2Dto.FromEvidence(snapshot.RecoverySelectionEvidence)
        };
}

internal sealed class ProtectedGraphRecoverySelectionEvidenceV2Dto
{
    public string? RecoveryPolicyId { get; set; }
    public long? SourceGeneration { get; set; }
    public List<Guid>? SelectedRootNodeIds { get; set; }

    internal ProtectedGraphRecoverySelectionEvidenceV2 ToEvidence()
        => new(
            ProtectionDtoHelpers.Required(RecoveryPolicyId, nameof(RecoveryPolicyId)),
            SourceGeneration ?? throw ProtectionDtoHelpers.Missing(nameof(SourceGeneration)),
            SelectedRootNodeIds ?? throw ProtectionDtoHelpers.Missing(nameof(SelectedRootNodeIds)));

    internal static ProtectedGraphRecoverySelectionEvidenceV2Dto FromEvidence(ProtectedGraphRecoverySelectionEvidenceV2 evidence)
        => new()
        {
            RecoveryPolicyId = evidence.RecoveryPolicyId,
            SourceGeneration = evidence.SourceGeneration,
            SelectedRootNodeIds = evidence.SelectedRootNodeIds.ToList()
        };
}
