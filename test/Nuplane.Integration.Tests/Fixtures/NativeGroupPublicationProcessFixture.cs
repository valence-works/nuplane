using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests.Fixtures;

/// <summary>Owns two incomplete v1 roots sharing one external state slot and a prepared v2 group payload.</summary>
internal sealed class NativeGroupPublicationProcessFixture : IDisposable
{
    private const string ZeroDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly PackageStoreFixture _fixture = new();
    private readonly List<PhysicalStoreDirectoryHandle> _handles = [];
    private bool _disposed;

    private NativeGroupPublicationProcessFixture() { }

    internal IPhysicalStoreFileSystem Files { get; private set; } = null!;
    internal RootMembershipRegistry Registry { get; private set; } = null!;
    internal string RootAPath { get; private set; } = null!;
    internal string RootBPath { get; private set; } = null!;
    internal string SharedParentPath { get; private set; } = null!;
    internal string SharedStatePath { get; private set; } = null!;
    internal string LocalStateAPath { get; private set; } = null!;
    internal string LocalStateBPath { get; private set; } = null!;
    internal string NextStatePath { get; private set; } = null!;
    internal StoreStateRecord PriorState { get; private set; } = null!;
    internal StoreStateRecord NextState { get; private set; } = null!;
    internal GroupRoot[] Roots { get; private set; } = [];
    internal GroupRoot FirstParticipant => Roots.OrderBy(static root => root.Identity,
        PhysicalRootIdentityComparer.Instance).First();
    internal GroupRoot UntouchedPeer => Roots.Single(root => root.Identity != FirstParticipant.Identity);
    internal PhysicalFileIdentity PriorSharedStateIdentity { get; private set; } = null!;
    internal string StagePath => Path.Combine(SharedParentPath,
        $".nuplane-group-{NextState.ProtectionBundle!.PublicationId:N}.tmp");
    internal string BackupPath => Path.Combine(SharedParentPath,
        $".nuplane-group-{NextState.ProtectionBundle!.PublicationId:N}.bak");

    internal static async Task<NativeGroupPublicationProcessFixture> CreateAsync()
    {
        var fixture = new NativeGroupPublicationProcessFixture();
        try
        {
            await fixture.InitializeAsync().ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    internal string WriteRequest(string operationId, string checkpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new
        {
            operationId,
            roots = Roots.OrderBy(static root => root.Identity, PhysicalRootIdentityComparer.Instance)
                .Select(static root => new
                {
                    rootPath = root.Path,
                    memberId = root.SharedMemberId,
                    enrollmentEpoch = root.Ledger.EnrollmentEpoch
                }).ToArray(),
            sharedStatePath = SharedStatePath,
            nextStatePath = NextStatePath,
            checkpoint,
            checkpointRootFileId = FirstParticipant.Identity.HandleIdentity.FileId
        };
        var path = _fixture.GetPath($"process-requests/{operationId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(request,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return path;
    }

    internal RootMembershipRecord ReadLedger(GroupRoot root) => Registry.ReadCandidate(root.Handle);

    internal StoreStateRecord ReadSharedState()
    {
        using var file = Files.OpenFileChildNoFollow(SharedParent, Path.GetFileName(SharedStatePath), FileAccess.Read);
        using var stream = new MemoryStream(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes));
        return new StoreStateSerializer().ReadPayloadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal PhysicalFileIdentity? InspectSharedStateIdentity()
        => Files.InspectChildNoFollow(SharedParent, Path.GetFileName(SharedStatePath))?.Identity;

    internal string ReadStateDigest(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal string[] FindTransactionArtifacts()
        => Directory.EnumerateFiles(SharedParentPath)
            .Select(Path.GetFileName)
            .Where(static name => name is not null && name.StartsWith(".nuplane-group-", StringComparison.Ordinal))
            .Select(static name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal SortedDictionary<string, SnapshotEntry> CaptureDataSnapshot()
    {
        var snapshot = new SortedDictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        foreach (var root in Roots)
        {
            using var control = Files.OpenDirectoryChildNoFollow(root.Handle,
                RootMembershipRegistry.ControlDirectoryName);
            var ledgerEntry = Files.InspectChildNoFollow(control, RootMembershipRegistry.LedgerName)
                ?? throw new FileNotFoundException("The root membership ledger disappeared during snapshot capture.");
            var ledgerPath = Path.Combine(root.Path, RootMembershipRegistry.ControlDirectoryName,
                RootMembershipRegistry.LedgerName);
            snapshot.Add($"{root.Name}/ledger", Snapshot(ledgerPath, ledgerEntry.Identity));
        }
        snapshot.Add("shared/state", Snapshot(SharedStatePath, InspectSharedStateIdentity()));
        snapshot.Add("local/a", Snapshot(LocalStateAPath, Files.InspectChildNoFollow(LocalParentA,
            Path.GetFileName(LocalStateAPath))?.Identity));
        snapshot.Add("local/b", Snapshot(LocalStateBPath, Files.InspectChildNoFollow(LocalParentB,
            Path.GetFileName(LocalStateBPath))?.Identity));
        foreach (var path in Directory.EnumerateFiles(SharedParentPath))
        {
            var name = Path.GetFileName(path);
            var entry = Files.InspectChildNoFollow(SharedParent, name)
                ?? throw new FileNotFoundException("A shared-state entry disappeared during snapshot capture.", path);
            snapshot[$"shared/{name}"] = Snapshot(path, entry.Identity);
        }
        return snapshot;
    }

    private PhysicalStoreDirectoryHandle SharedParent { get; set; } = null!;
    private PhysicalStoreDirectoryHandle LocalParentA { get; set; } = null!;
    private PhysicalStoreDirectoryHandle LocalParentB { get; set; } = null!;

    private async Task InitializeAsync()
    {
        Files = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        RootAPath = _fixture.CreateDirectory("root-a");
        RootBPath = _fixture.CreateDirectory("root-b");
        SharedParentPath = _fixture.CreateDirectory("shared");
        var localParentPathA = _fixture.CreateDirectory("local-a");
        var localParentPathB = _fixture.CreateDirectory("local-b");
        SharedStatePath = _fixture.CreateStateSlot("shared/group-state.json");
        LocalStateAPath = _fixture.CreateStateSlot("local-a/state.json");
        LocalStateBPath = _fixture.CreateStateSlot("local-b/state.json");
        NextStatePath = _fixture.GetPath("prepared/next-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(NextStatePath)!);

        var rootA = OwnedProcessDirectory.Open(Files, RootAPath);
        var rootB = OwnedProcessDirectory.Open(Files, RootBPath);
        SharedParent = OwnedProcessDirectory.Open(Files, SharedParentPath);
        LocalParentA = OwnedProcessDirectory.Open(Files, localParentPathA);
        LocalParentB = OwnedProcessDirectory.Open(Files, localParentPathB);
        _handles.AddRange([rootA, rootB, SharedParent, LocalParentA, LocalParentB]);
        Registry = new RootMembershipRegistry(Files, new StoreStateSerializer());
        PriorState = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
        WriteState(SharedStatePath, PriorState);
        WriteState(LocalStateAPath, PriorState);
        WriteState(LocalStateBPath, PriorState);
        var rootAIdentity = new PhysicalRootIdentity(Files.InspectHandle(rootA).Identity);
        var rootBIdentity = new PhysicalRootIdentity(Files.InspectHandle(rootB).Identity);
        var priorBodyDigest = ProtectionDigest.StateBody(PriorState);

        var initial = new[]
        {
            new GroupRoot("a", RootAPath, "shared-a", "local-a", rootA, rootAIdentity),
            new GroupRoot("b", RootBPath, "shared-b", "local-b", rootB, rootBIdentity)
        };
        Roots = initial;
        for (var index = 0; index < initial.Length; index++)
        {
            var root = initial[index];
            var declarations = new[]
            {
                new RootMemberRecord(root.SharedMemberId, SharedStatePath, new RootMemberRecord.DeclaredBinding()),
                new RootMemberRecord(root.LocalMemberId, index == 0 ? LocalStateAPath : LocalStateBPath,
                    new RootMemberRecord.DeclaredBinding())
            };
            Registry.InitializeIncomplete(root.Handle, root.Identity, 1, declarations, true, CancellationToken.None);
            var localParent = index == 0 ? LocalParentA : LocalParentB;
            var localPath = index == 0 ? LocalStateAPath : LocalStateBPath;
            var locations = new Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>
            {
                [root.SharedMemberId] = (SharedParent, Path.GetFileName(SharedStatePath)),
                [root.LocalMemberId] = (localParent, Path.GetFileName(localPath))
            };
            root.Ledger = await Registry.BindDeclaredMembersAsync(root.Handle, root.Identity, 1,
                declarations, locations, true, CancellationToken.None).ConfigureAwait(false);
            var localProtection = Protect(PriorState, root.Identity, root.LocalMemberId, 1);
            var localState = PriorState with { ProtectionRecord = localProtection };
            WriteState(localPath, localState);
            var observation = new PhysicalStoreIdentity(Files).ObserveStateSlot(localParent, Path.GetFileName(localPath));
            var acknowledgedMembers = root.Ledger.Members.Select(member => member.MemberId == root.LocalMemberId
                ? new RootMemberRecord(member.MemberId, member.ConfiguredLocator,
                    new RootMemberRecord.AcknowledgedBinding(observation.Slot, observation.FileIdentity, localProtection))
                : member).ToArray();
            var candidate = new RootMembershipRecord(root.Ledger.SchemaVersion, root.Ledger.RootIdentity,
                root.Ledger.EnrollmentEpoch, root.Ledger.Status, acknowledgedMembers, root.Ledger.TargetMemberIds,
                root.Ledger.RetiredMembers, null, ZeroDigest);
            root.Ledger = new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity,
                candidate.EnrollmentEpoch, candidate.Status, candidate.Members, candidate.TargetMemberIds,
                candidate.RetiredMembers, null, ProtectionDigest.Ledger(candidate));
            WriteLedger(root, root.Ledger);
        }

        PriorSharedStateIdentity = Files.InspectChildNoFollow(SharedParent, Path.GetFileName(SharedStatePath))!.Identity;
        var nextBody = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        var nextDigest = ProtectionDigest.StateBody(nextBody);
        var empty = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null, []);
        var rows = Roots.Select(root => new PackageProtectionBundleRootRow(root.Identity, 1,
            root.SharedMemberId, 1, 1, nextDigest, empty, empty, [], false)).ToArray();
        var bundle = new PackageProtectionBundle(PackageProtectionBundle.CurrentSchemaVersion,
            Guid.NewGuid(), Guid.NewGuid(), 1, nextDigest, rows);
        NextState = nextBody with { ProtectionBundle = bundle };
        WriteState(NextStatePath, NextState);
    }

    private static PackageProtectionRecord Protect(StoreStateRecord body, PhysicalRootIdentity root,
        string memberId, long revision)
    {
        var empty = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, 1,
            memberId, revision, ProtectionDigest.StateBody(body), ZeroDigest, empty, empty, [], false);
        return new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, 1, memberId,
            revision, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), empty, empty, [], false);
    }

    private void WriteState(string path, StoreStateRecord state)
    {
        using var stream = new MemoryStream();
        new StoreStateSerializer().WritePayloadAsync(stream, state, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllBytes(path, stream.ToArray());
    }

    private void WriteLedger(GroupRoot root, RootMembershipRecord ledger)
        => File.WriteAllBytes(Path.Combine(root.Path, RootMembershipRegistry.ControlDirectoryName,
            RootMembershipRegistry.LedgerName), new RootMembershipPayloadSerializer().Serialize(ledger));

    private static SnapshotEntry Snapshot(string path, PhysicalFileIdentity? identity)
    {
        var bytes = File.ReadAllBytes(path);
        return new SnapshotEntry(identity, bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var handle in _handles.AsEnumerable().Reverse())
            handle.Dispose();
        _fixture.Dispose();
    }

    internal sealed class GroupRoot(string name, string path, string sharedMemberId, string localMemberId,
        PhysicalStoreDirectoryHandle handle, PhysicalRootIdentity identity)
    {
        internal string Name { get; } = name;
        internal string Path { get; } = path;
        internal string SharedMemberId { get; } = sharedMemberId;
        internal string LocalMemberId { get; } = localMemberId;
        internal PhysicalStoreDirectoryHandle Handle { get; } = handle;
        internal PhysicalRootIdentity Identity { get; } = identity;
        internal RootMembershipRecord Ledger { get; set; } = null!;
    }

    internal sealed record SnapshotEntry(PhysicalFileIdentity? Identity, long Length, string Digest);
}
