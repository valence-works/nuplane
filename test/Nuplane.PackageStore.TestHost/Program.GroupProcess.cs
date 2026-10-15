using System.Security.Cryptography;
using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.PackageStore.TestHost;

internal static partial class Program
{
    private static async Task<int> RunGroupPublishAsync(string requestPath)
    {
        var request = await ReadGroupProcessRequestAsync(requestPath).ConfigureAwait(false);
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);

        var files = CreateFileSystem();
        var roots = new List<PhysicalStoreDirectoryHandle>();
        try
        {
            var registry = new RootMembershipRegistry(files, new StoreStateSerializer());
            var rootData = new List<(GroupRootRequest Request, PhysicalStoreDirectoryHandle Handle,
                RootMembershipRecord Ledger, PhysicalRootIdentity Identity)>();
            foreach (var rootRequest in request.Roots)
            {
                var handle = OwnedProcessDirectory.Open(files, rootRequest.RootPath);
                roots.Add(handle);
                var identity = new PhysicalRootIdentity(files.InspectHandle(handle).Identity);
                var ledger = registry.ReadCandidate(handle);
                rootData.Add((rootRequest, handle, ledger, identity));
            }

            var nextState = await ReadStateFileAsync(request.NextStatePath, CancellationToken.None).ConfigureAwait(false);
            var bundle = nextState.ProtectionBundle
                ?? throw new InvalidDataException("A group publication requires the exact prepared next-state bundle.");
            var first = rootData.OrderBy(static item => item.Identity, PhysicalRootIdentityComparer.Instance).First();
            var selectedFirst = first.Ledger.Members.Single(member => member.MemberId == first.Request.MemberId);
            var slot = GetStateSlot(selectedFirst.Binding);
            var priorIdentity = GetStateFileIdentity(selectedFirst.Binding);
            using var sharedParent = OwnedProcessDirectory.Open(files,
                Path.GetDirectoryName(request.SharedStatePath)!);
            var priorState = await ReadStateAtSlotAsync(files, sharedParent, slot, priorIdentity,
                CancellationToken.None).ConfigureAwait(false);
            var priorBodyDigest = ProtectionDigest.StateBody(priorState);
            var transactionStem = $".nuplane-group-{bundle.PublicationId:N}";
            var stagedName = transactionStem + ".tmp";
            var backupName = priorIdentity is null ? null : transactionStem + ".bak";
            var participants = rootData.Select(item =>
            {
                var member = item.Ledger.Members.Single(candidate => candidate.MemberId == item.Request.MemberId);
                var row = bundle.Rows.Single(candidate => candidate.RootIdentity == item.Identity);
                var revision = GetRevision(member.Binding);
                return new GroupPublicationParticipantV2(item.Identity, item.Ledger.EnrollmentEpoch, member,
                    item.Ledger.Status, item.Ledger.SchemaVersion, item.Ledger.LedgerDigest, revision, null,
                    GetStateFileIdentity(member.Binding), checked(revision + 1), row.ProtectionDigest,
                    stagedName, backupName);
            }).ToArray();
            var descriptor = new GroupPublicationDescriptorV2(bundle.PublicationId, bundle.LogicalMemberId,
                slot, 0, priorBodyDigest, GroupPublicationDescriptorV2.ZeroDigest,
                bundle.StateGeneration, bundle.StateBodyDigest, bundle.BundleDigest, participants);
            var groupRoots = rootData.Select(item => new RootMembershipRegistry.NativeGroupRootRequest(
                item.Handle, item.Identity, item.Ledger.EnrollmentEpoch)).ToArray();
            var checkpoint = ParseRequiredGroupCheckpoint(request.Checkpoint);

            var result = await registry.PublishNativeGroupAsync(descriptor, groupRoots, nextState,
                CancellationToken.None, (point, rootIdentity) =>
                    PauseAtGroupCheckpoint(request, checkpoint, point, rootIdentity, descriptor,
                        rootData, registry, files, sharedParent)).ConfigureAwait(false);
            await WriteJsonLineAsync(new OperationResult("published", request.OperationId, Environment.ProcessId,
                result.Select(static ledger => ledger.Status.ToString()).Distinct().Single(),
                null, false, null, null)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            foreach (var root in roots.AsEnumerable().Reverse())
                root.Dispose();
        }
    }

    private static void PauseAtGroupCheckpoint(GroupProcessRequest request,
        RootMembershipRegistry.NativeGroupPublicationPoint expected,
        RootMembershipRegistry.NativeGroupPublicationPoint actual,
        PhysicalRootIdentity? pointRoot,
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<(GroupRootRequest Request, PhysicalStoreDirectoryHandle Handle,
            RootMembershipRecord Ledger, PhysicalRootIdentity Identity)> roots,
        RootMembershipRegistry registry,
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle sharedParent)
    {
        if (actual != expected)
            return;
        if (expected == RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished &&
            pointRoot?.HandleIdentity.FileId != request.CheckpointRootFileId)
            return;

        var current = roots.Select(item =>
        {
            var ledger = registry.ReadCandidate(item.Handle);
            var pending = ledger.PendingGroupPublicationV2;
            return new GroupRootCheckpoint(item.Identity.HandleIdentity.FileId, ledger.Status.ToString(),
                ledger.SchemaVersion, ledger.LedgerDigest, pending?.Phase.ToString(), pending?.Resolution.ToString(),
                pending?.StagedStateFileIdentity?.FileId, pending?.BackupStateFileIdentity?.FileId);
        }).ToArray();
        var stageName = descriptor.Participants[0].StagedName;
        var stage = files.InspectChildNoFollow(sharedParent, stageName);
        string? stageDigest = null;
        if (stage is { Kind: PhysicalStoreEntryKind.RegularFile })
        {
            using var file = files.OpenFileChildNoFollow(sharedParent, stageName, FileAccess.Read);
            stageDigest = Convert.ToHexString(SHA256.HashData(
                files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
        }
        var sharedState = files.InspectChildNoFollow(sharedParent,
            descriptor.SharedStateSlot.CanonicalBasename);
        string? sharedStateDigest = null;
        if (sharedState is { Kind: PhysicalStoreEntryKind.RegularFile })
        {
            using var file = files.OpenFileChildNoFollow(sharedParent,
                descriptor.SharedStateSlot.CanonicalBasename, FileAccess.Read);
            sharedStateDigest = Convert.ToHexString(SHA256.HashData(
                files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
        }
        WriteJsonLineAsync(new GroupCheckpointMessage("checkpoint", request.OperationId,
            Environment.ProcessId, actual.ToString(), pointRoot?.HandleIdentity.FileId,
            descriptor.IntentDigest, current, stage?.Identity.FileId, stageDigest,
            sharedState?.Identity.FileId, sharedStateDigest)).GetAwaiter().GetResult();
        WaitForContinueCommand(request.OperationId, "native group publication checkpoint", "group checkpoint");
    }

    private static async Task<GroupProcessRequest> ReadGroupProcessRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<GroupProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The group process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            request.Roots is null || request.Roots.Length != 2 ||
            request.Roots.Any(static root => root is null || string.IsNullOrWhiteSpace(root.RootPath) ||
                !Path.IsPathFullyQualified(root.RootPath) || string.IsNullOrWhiteSpace(root.MemberId) ||
                root.EnrollmentEpoch <= 0) ||
            request.Roots.Select(static root => root.RootPath).Distinct(StringComparer.Ordinal).Count() != 2 ||
            string.IsNullOrWhiteSpace(request.SharedStatePath) || !Path.IsPathFullyQualified(request.SharedStatePath) ||
            string.IsNullOrWhiteSpace(request.NextStatePath) || !Path.IsPathFullyQualified(request.NextStatePath) ||
            string.IsNullOrWhiteSpace(request.CheckpointRootFileId))
            throw new InvalidDataException("The group process request fields were invalid.");
        _ = ParseRequiredGroupCheckpoint(request.Checkpoint);
        return request;
    }

    private static RootMembershipRegistry.NativeGroupPublicationPoint ParseRequiredGroupCheckpoint(string value)
    {
        if (!Enum.TryParse<RootMembershipRegistry.NativeGroupPublicationPoint>(value, false, out var point) ||
            !Enum.IsDefined(point))
            throw new InvalidDataException("The native group checkpoint is not supported.");
        return point;
    }

    private static StateSlotIdentity GetStateSlot(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.ExistingUnprotectedBinding existing => existing.StateSlot,
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
        RootMemberRecord.BundleAcknowledgedBinding bundled => bundled.StateSlot,
        RootMemberRecord.ProspectiveBinding prospective => new StateSlotIdentity(prospective.VerifiedParentIdentity,
            prospective.NameSemantics, prospective.RequestedBasename),
        _ => throw new InvalidDataException("A native group participant has no retained shared state slot.")
    };

    private static PhysicalFileIdentity? GetStateFileIdentity(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.ExistingUnprotectedBinding existing => existing.ObservedStateFileIdentity,
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
        RootMemberRecord.BundleAcknowledgedBinding bundled => bundled.ObservedStateFileIdentity,
        RootMemberRecord.ProspectiveBinding => null,
        _ => throw new InvalidDataException("A native group participant has no retained shared state identity.")
    };

    private static long GetRevision(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ProtectionRecord.Revision,
        RootMemberRecord.BundleAcknowledgedBinding bundled => bundled.RootRow.Revision,
        RootMemberRecord.ExistingUnprotectedBinding or RootMemberRecord.ProspectiveBinding => 0,
        _ => throw new InvalidDataException("A native group participant has an unsupported prior binding.")
    };

    private static async Task<StoreStateRecord> ReadStateAtSlotAsync(IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot, PhysicalFileIdentity? identity,
        CancellationToken cancellationToken)
    {
        if (identity is null)
            return StoreStateRecord.Empty();
        var entry = files.InspectChildNoFollow(parent, slot.CanonicalBasename);
        if (entry is null || entry.Identity != identity || entry.Kind != PhysicalStoreEntryKind.RegularFile)
            throw new InvalidDataException("The current shared state does not match its prior participant identity.");
        using var file = files.OpenFileChildNoFollow(parent, slot.CanonicalBasename, FileAccess.Read);
        using var stream = new MemoryStream(files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes));
        return await new StoreStateSerializer().ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoreStateRecord> ReadStateFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes);
        return await new StoreStateSerializer().ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private sealed record GroupRootRequest(string RootPath, string MemberId, long EnrollmentEpoch);
    private sealed record GroupProcessRequest(string OperationId, GroupRootRequest[] Roots,
        string SharedStatePath, string NextStatePath, string Checkpoint, string CheckpointRootFileId);
    private sealed record GroupRootCheckpoint(string RootFileId, string Status, int SchemaVersion,
        string LedgerDigest, string? Phase, string? Resolution, string? StagedFileId, string? BackupFileId);
    private sealed record GroupCheckpointMessage(string Kind, string OperationId, int ProcessId,
        string Point, string? PointRootFileId, string IntentDigest, GroupRootCheckpoint[] Roots,
        string? StageFileId, string? StageDigest, string? SharedStateFileId, string? SharedStateDigest);
}
