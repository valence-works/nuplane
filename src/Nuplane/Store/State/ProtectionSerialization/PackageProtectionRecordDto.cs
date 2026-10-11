using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.State.ProtectionSerialization;

internal sealed class PackageProtectionRecordDto
{
    public int? SchemaVersion { get; set; }
    public PhysicalRootIdentityDto? RootIdentity { get; set; }
    public long? EnrollmentEpoch { get; set; }
    public string? MemberId { get; set; }
    public long? Revision { get; set; }
    public string? StateBodyDigest { get; set; }
    public string? ProtectionDigest { get; set; }
    public PackageProtectionClosureDto? ActiveClosure { get; set; }
    public PackageProtectionClosureDto? RecoverableClosure { get; set; }
    public List<RetiredGraphEvidenceDto?>? RetiredGraphs { get; set; }
    public bool? LegacyUnknownRecovery { get; set; }

    internal PackageProtectionRecord ToRecord()
    {
        if (SchemaVersion != PackageProtectionRecord.CurrentSchemaVersion)
            throw new JsonException("The package-protection schema version is unsupported.");
        var stateBodyDigest = ProtectionDtoHelpers.Required(StateBodyDigest, nameof(StateBodyDigest));
        var protectionDigest = ProtectionDtoHelpers.Required(ProtectionDigest, nameof(ProtectionDigest));
        PackageProtectionRecordJsonConverter.ValidateDigest(stateBodyDigest, "stateBodyDigest");
        PackageProtectionRecordJsonConverter.ValidateDigest(protectionDigest, "protectionDigest");
        var retired = ProtectionDtoHelpers.MapRequired(RetiredGraphs, nameof(RetiredGraphs), static item => item.ToEvidence());

        return new PackageProtectionRecord(
            SchemaVersion.Value,
            ProtectionDtoHelpers.Required(RootIdentity, nameof(RootIdentity)).ToIdentity(),
            EnrollmentEpoch ?? throw ProtectionDtoHelpers.Missing(nameof(EnrollmentEpoch)),
            ProtectionDtoHelpers.Required(MemberId, nameof(MemberId)),
            Revision ?? throw ProtectionDtoHelpers.Missing(nameof(Revision)),
            stateBodyDigest,
            protectionDigest,
            ProtectionDtoHelpers.Required(ActiveClosure, nameof(ActiveClosure)).ToClosure(),
            ProtectionDtoHelpers.Required(RecoverableClosure, nameof(RecoverableClosure)).ToClosure(),
            retired,
            LegacyUnknownRecovery ?? throw ProtectionDtoHelpers.Missing(nameof(LegacyUnknownRecovery)));
    }

    internal static PackageProtectionRecordDto FromRecord(PackageProtectionRecord record)
        => new()
        {
            SchemaVersion = record.SchemaVersion,
            RootIdentity = PhysicalRootIdentityDto.FromIdentity(record.RootIdentity),
            EnrollmentEpoch = record.EnrollmentEpoch,
            MemberId = record.MemberId,
            Revision = record.Revision,
            StateBodyDigest = record.StateBodyDigest,
            ProtectionDigest = record.ProtectionDigest,
            ActiveClosure = PackageProtectionClosureDto.FromClosure(record.ActiveClosure),
            RecoverableClosure = PackageProtectionClosureDto.FromClosure(record.RecoverableClosure),
            RetiredGraphs = record.RetiredGraphs.Select(static item => (RetiredGraphEvidenceDto?)RetiredGraphEvidenceDto.FromEvidence(item)).ToList(),
            LegacyUnknownRecovery = record.LegacyUnknownRecovery
        };

}

internal sealed class PackageProtectionClosureDto
{
    public int? Knowledge { get; set; }
    public int? UnknownReason { get; set; }
    public List<ProtectedGraphSnapshotDto?>? Graphs { get; set; }

    internal PackageProtectionClosure ToClosure()
    {
        if (Knowledge is null || !Enum.IsDefined((PackageProtectionClosureKnowledge)Knowledge.Value))
            throw new JsonException("The protection closure has an invalid or missing knowledge tag.");

        var knowledge = (PackageProtectionClosureKnowledge)Knowledge.Value;
        PackageProtectionUnknownReasonCode? reason = null;
        if (UnknownReason is not null)
        {
            reason = (PackageProtectionUnknownReasonCode)UnknownReason.Value;
            if (!Enum.IsDefined(reason.Value))
                throw new JsonException("The protection closure has an invalid unknown reason.");
        }

        if (knowledge == PackageProtectionClosureKnowledge.Unknown)
        {
            if (reason is null || Graphs is not null)
                throw new JsonException("An unknown protection closure requires a known reason and no graph collection.");
            return new PackageProtectionClosure(knowledge, reason, graphs: null);
        }

        if (reason is not null || Graphs is null)
            throw new JsonException("A known protection closure requires a graph array and cannot carry an unknown reason.");

        var graphs = ProtectionDtoHelpers.MapRequired(Graphs, nameof(Graphs), static item => item.ToSnapshot());
        return new PackageProtectionClosure(knowledge, unknownReason: null, graphs);
    }

    internal static PackageProtectionClosureDto FromClosure(PackageProtectionClosure closure)
        => new()
        {
            Knowledge = (int)closure.Knowledge,
            UnknownReason = closure.UnknownReason is null ? null : (int)closure.UnknownReason.Value,
            Graphs = closure.Graphs?.Select(static item => (ProtectedGraphSnapshotDto?)ProtectedGraphSnapshotDto.FromSnapshot(item)).ToList()
        };
}

internal sealed class ProtectedGraphSnapshotDto
{
    public Guid? SnapshotId { get; set; }
    public string? GraphId { get; set; }
    public string? GenerationId { get; set; }
    public int? Disposition { get; set; }
    public List<PhysicalRootIdentityDto?>? Roots { get; set; }
    public List<PackageGraphRootSelectionDto?>? RequestedRoots { get; set; }
    public List<PackageGraphNodeIdentityDto?>? Nodes { get; set; }
    public List<PackageGraphEdgeIdentityDto?>? Edges { get; set; }
    public ProtectedGraphRecoverySelectionEvidenceDto? RecoverySelectionEvidence { get; set; }

    internal ProtectedGraphSnapshot ToSnapshot()
    {
        if (Disposition is null || !Enum.IsDefined((ProtectedGraphDisposition)Disposition.Value))
            throw new JsonException("A graph snapshot has an invalid or missing disposition.");

        return new ProtectedGraphSnapshot(
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

    internal static ProtectedGraphSnapshotDto FromSnapshot(ProtectedGraphSnapshot snapshot)
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
                : ProtectedGraphRecoverySelectionEvidenceDto.FromEvidence(snapshot.RecoverySelectionEvidence)
        };

}

internal sealed class PhysicalRootIdentityDto
{
    public PhysicalFileIdentityDto? HandleIdentity { get; set; }

    internal PhysicalRootIdentity ToIdentity()
        => new(HandleIdentity?.ToIdentity() ?? throw new JsonException("A physical root is missing its handle identity."));

    internal static PhysicalRootIdentityDto FromIdentity(PhysicalRootIdentity identity)
        => new() { HandleIdentity = PhysicalFileIdentityDto.FromIdentity(identity.HandleIdentity) };
}

internal sealed class PhysicalFileIdentityDto
{
    public string? Provider { get; set; }
    public string? VolumeOrDeviceId { get; set; }
    public string? FileId { get; set; }

    internal PhysicalFileIdentity ToIdentity()
        => new(
            Provider ?? throw new JsonException("A physical identity is missing its provider."),
            VolumeOrDeviceId ?? throw new JsonException("A physical identity is missing its volume/device ID."),
            FileId ?? throw new JsonException("A physical identity is missing its file ID."));

    internal static PhysicalFileIdentityDto FromIdentity(PhysicalFileIdentity identity)
        => new() { Provider = identity.Provider, VolumeOrDeviceId = identity.VolumeOrDeviceId, FileId = identity.FileId };
}

internal sealed class PackageGraphRootSelectionDto
{
    public PackageRequestDto? Request { get; set; }
    public Guid? SelectedNodeId { get; set; }

    internal PackageGraphRootSelection ToSelection()
        => new(
            Request?.ToRequest() ?? throw new JsonException("A requested root is missing its package request."),
            SelectedNodeId ?? throw new JsonException("A requested root is missing its selected node ID."));

    internal static PackageGraphRootSelectionDto FromSelection(PackageGraphRootSelection selection)
        => new() { Request = PackageRequestDto.FromRequest(selection.Request), SelectedNodeId = selection.SelectedNodeId };
}

internal sealed class PackageRequestDto
{
    public string? Id { get; set; }
    public string? VersionRange { get; set; }
    public string? FeedName { get; set; }
    public int? UpdatePolicy { get; set; }
    public string? SourceName { get; set; }

    internal PackageRequest ToRequest()
    {
        if (UpdatePolicy is null || !Enum.IsDefined((PackageUpdatePolicy)UpdatePolicy.Value))
            throw new JsonException("A package request has an invalid or missing update policy.");
        return new PackageRequest(
            Id ?? throw new JsonException("A package request is missing its package ID."),
            VersionRange ?? throw new JsonException("A package request is missing its version range."),
            FeedName,
            (PackageUpdatePolicy)UpdatePolicy.Value,
            SourceName ?? throw new JsonException("A package request is missing its source name."));
    }

    internal static PackageRequestDto FromRequest(PackageRequest request)
        => new()
        {
            Id = request.Id,
            VersionRange = request.VersionRange,
            FeedName = request.FeedName,
            UpdatePolicy = (int)request.UpdatePolicy,
            SourceName = request.SourceName
        };
}

internal sealed class PackageGraphNodeIdentityDto
{
    public Guid? NodeId { get; set; }
    public PackageInstallIdentityDto? Install { get; set; }

    internal PackageGraphNodeIdentity ToNode()
        => new(
            NodeId ?? throw new JsonException("A package graph node is missing its node ID."),
            Install?.ToInstallIdentity() ?? throw new JsonException("A package graph node is missing its install identity."));

    internal static PackageGraphNodeIdentityDto FromNode(PackageGraphNodeIdentity node)
        => new() { NodeId = node.NodeId, Install = PackageInstallIdentityDto.FromInstallIdentity(node.Install) };
}

internal sealed class PackageInstallIdentityDto
{
    public PhysicalRootIdentityDto? Root { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public string? RootRelativeInstallPath { get; set; }
    public PhysicalFileIdentityDto? DirectoryIdentity { get; set; }
    public string? CompletionIdentity { get; set; }
    public string? VerifiedArchiveHash { get; set; }

    internal PackageInstallIdentity ToInstallIdentity()
        => new(
            Root?.ToIdentity() ?? throw new JsonException("An install identity is missing its root."),
            PackageId ?? throw new JsonException("An install identity is missing its package ID."),
            Version ?? throw new JsonException("An install identity is missing its version."),
            RootRelativeInstallPath ?? throw new JsonException("An install identity is missing its relative path."),
            DirectoryIdentity?.ToIdentity() ?? throw new JsonException("An install identity is missing its native directory identity."),
            CompletionIdentity ?? throw new JsonException("An install identity is missing its completion identity."),
            VerifiedArchiveHash);

    internal static PackageInstallIdentityDto FromInstallIdentity(PackageInstallIdentity install)
        => new()
        {
            Root = PhysicalRootIdentityDto.FromIdentity(install.Root),
            PackageId = install.PackageId,
            Version = install.Version,
            RootRelativeInstallPath = install.RootRelativeInstallPath,
            DirectoryIdentity = PhysicalFileIdentityDto.FromIdentity(install.DirectoryIdentity),
            CompletionIdentity = install.CompletionIdentity,
            VerifiedArchiveHash = install.VerifiedArchiveHash
        };
}

internal sealed class PackageGraphEdgeIdentityDto
{
    public Guid? FromNodeId { get; set; }
    public Guid? ToNodeId { get; set; }
    public string? RequestedPackageId { get; set; }
    public string? RequestedVersionRange { get; set; }
    public string? TargetFramework { get; set; }
    public bool? IsOptional { get; set; }

    internal PackageGraphEdgeIdentity ToEdge()
        => new(
            FromNodeId ?? throw new JsonException("A graph edge is missing its declaring node ID."),
            ToNodeId ?? throw new JsonException("A graph edge is missing its selected node ID."),
            RequestedPackageId ?? throw new JsonException("A graph edge is missing its requested package ID."),
            RequestedVersionRange ?? throw new JsonException("A graph edge is missing its version range."),
            TargetFramework ?? throw new JsonException("A graph edge is missing its target framework."),
            IsOptional ?? throw new JsonException("A graph edge is missing its optionality."));

    internal static PackageGraphEdgeIdentityDto FromEdge(PackageGraphEdgeIdentity edge)
        => new()
        {
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            RequestedPackageId = edge.RequestedPackageId,
            RequestedVersionRange = edge.RequestedVersionRange,
            TargetFramework = edge.TargetFramework,
            IsOptional = edge.IsOptional
        };
}

internal sealed class ProtectedGraphRecoverySelectionEvidenceDto
{
    public string? RecoveryPolicyId { get; set; }
    public long? SourceRevision { get; set; }
    public List<Guid>? SelectedRootNodeIds { get; set; }

    internal ProtectedGraphRecoverySelectionEvidence ToEvidence()
        => new(
            RecoveryPolicyId ?? throw new JsonException("Recovery evidence is missing its policy ID."),
            SourceRevision ?? throw new JsonException("Recovery evidence is missing its source revision."),
            SelectedRootNodeIds ?? throw new JsonException("Recovery evidence is missing its selected root node IDs."));

    internal static ProtectedGraphRecoverySelectionEvidenceDto FromEvidence(ProtectedGraphRecoverySelectionEvidence evidence)
        => new()
        {
            RecoveryPolicyId = evidence.RecoveryPolicyId,
            SourceRevision = evidence.SourceRevision,
            SelectedRootNodeIds = evidence.SelectedRootNodeIds.ToList()
        };
}

internal sealed class RetiredGraphEvidenceDto
{
    public Guid? SnapshotId { get; set; }
    public string? GraphId { get; set; }
    public string? GenerationId { get; set; }
    public long? RetiringEpoch { get; set; }
    public long? RetiringRevision { get; set; }
    public int? Reason { get; set; }
    public string? ProofDigest { get; set; }

    internal RetiredGraphEvidence ToEvidence()
    {
        if (Reason is null || !Enum.IsDefined((RetiredGraphReason)Reason.Value))
            throw new JsonException("Retirement evidence has an invalid or missing reason.");

        var proofDigest = ProofDigest ?? throw new JsonException("Retirement evidence is missing its proof digest.");
        PackageProtectionRecordJsonConverter.ValidateDigest(proofDigest, "retiredGraphs.proofDigest");
        return new RetiredGraphEvidence(
            SnapshotId ?? throw new JsonException("Retirement evidence is missing its snapshot ID."),
            GraphId ?? throw new JsonException("Retirement evidence is missing its graph ID."),
            GenerationId ?? throw new JsonException("Retirement evidence is missing its generation ID."),
            RetiringEpoch ?? throw new JsonException("Retirement evidence is missing its epoch."),
            RetiringRevision ?? throw new JsonException("Retirement evidence is missing its revision."),
            (RetiredGraphReason)Reason.Value,
            proofDigest);
    }

    internal static RetiredGraphEvidenceDto FromEvidence(RetiredGraphEvidence evidence)
        => new()
        {
            SnapshotId = evidence.SnapshotId,
            GraphId = evidence.GraphId,
            GenerationId = evidence.GenerationId,
            RetiringEpoch = evidence.RetiringEpoch,
            RetiringRevision = evidence.RetiringRevision,
            Reason = (int)evidence.Reason,
            ProofDigest = evidence.ProofDigest
        };
}

internal static class ProtectionDtoHelpers
{
    internal static T Required<T>(T? value, string name) where T : class
        => value ?? throw Missing(name);

    internal static JsonException Missing(string name) => new($"The protection payload is missing '{name}'.");

    internal static List<TResult> MapRequired<TSource, TResult>(
        List<TSource?>? items,
        string fieldName,
        Func<TSource, TResult> map)
        where TSource : class
    {
        if (items is null)
            throw new JsonException($"The protection payload is missing required array '{fieldName}'.");

        var result = new List<TResult>(items.Count);
        foreach (var item in items)
        {
            if (item is null)
                throw new JsonException($"Protection array '{fieldName}' cannot contain null items.");
            result.Add(map(item));
        }
        return result;
    }
}
