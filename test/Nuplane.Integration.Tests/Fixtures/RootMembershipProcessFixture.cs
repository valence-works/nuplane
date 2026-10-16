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

internal enum RootMembershipPriorShape
{
    Prospective,
    ExistingUnprotected,
    Acknowledged
}

/// <summary>Owns a seeded, valid two-member membership fixture with states outside the install root.</summary>
internal sealed class RootMembershipProcessFixture : IDisposable
{
    private const string ZeroDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly PackageStoreFixture _fixture = new();
    private readonly Dictionary<string, PhysicalStoreDirectoryHandle> _parents = new(StringComparer.Ordinal);
    private bool _disposed;

    private RootMembershipProcessFixture() { }

    internal IPhysicalStoreFileSystem Files { get; private set; } = null!;
    internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
    internal string StateParentPath { get; private set; } = null!;
    internal string SiblingStateParentPath { get; private set; } = null!;
    internal string RootPath => _fixture.PackageInstallRoot;
    internal string StateFilePath => _fixture.StateFilePath;
    internal string SiblingStateFilePath { get; private set; } = null!;
    internal RootMembershipRecord Initial { get; private set; } = null!;
    internal RootMembershipRegistry Registry { get; private set; } = null!;
    internal string? InitialTargetStateDigest { get; private set; }
    internal string InitialSiblingStateDigest { get; private set; } = null!;
    internal PhysicalFileIdentity? InitialTargetIdentity { get; private set; }
    internal PhysicalFileIdentity InitialSiblingIdentity { get; private set; } = null!;
    internal StateSlotIdentity TargetSlot { get; private set; } = null!;
    internal StateSlotIdentity SiblingSlot { get; private set; } = null!;
    internal IReadOnlyDictionary<string, PhysicalStoreDirectoryHandle> Parents => _parents;

    internal static async Task<RootMembershipProcessFixture> CreateAsync(RootMembershipPriorShape priorShape)
    {
        var fixture = new RootMembershipProcessFixture();
        try
        {
            await fixture.InitializeAsync(priorShape).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    internal string WriteRequest(string operationId, string targetMemberId, string? checkpoint, int nextStateDay = 7)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new
        {
            operationId,
            rootPath = RootPath,
            targetMemberId,
            memberParentPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target"] = StateParentPath,
                ["sibling"] = SiblingStateParentPath
            },
            checkpoint,
            nextStateDay
        };
        var path = _fixture.GetPath($"process-requests/{operationId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(request,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return path;
    }

    internal PhysicalStoreEntryInfo? InspectState(string memberId)
    {
        var member = Initial.Members.Single(item => item.MemberId == memberId);
        var slot = SlotFor(member);
        return Files.InspectChildNoFollow(_parents[memberId], slot.CanonicalBasename);
    }

    internal string? ReadStateDigest(string memberId)
    {
        var member = Initial.Members.Single(item => item.MemberId == memberId);
        var slot = SlotFor(member);
        var entry = Files.InspectChildNoFollow(_parents[memberId], slot.CanonicalBasename);
        if (entry is null)
            return null;
        using var file = Files.OpenFileChildNoFollow(_parents[memberId], slot.CanonicalBasename, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
    }

    internal async Task<StoreStateRecord?> ReadStateAsync(string memberId, CancellationToken cancellationToken)
    {
        var entry = InspectState(memberId);
        if (entry is null)
            return null;
        var member = Initial.Members.Single(item => item.MemberId == memberId);
        var slot = SlotFor(member);
        using var file = Files.OpenFileChildNoFollow(_parents[memberId], slot.CanonicalBasename, FileAccess.Read);
        using var payload = new MemoryStream(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes));
        return await new StoreStateSerializer().ReadPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    internal SortedDictionary<string, SnapshotEntry> CaptureDataSnapshot()
    {
        var entries = new SortedDictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        entries.Add("root", SnapshotOf(Files.InspectHandle(Root), null));
        using (var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName))
        {
            entries.Add("control", SnapshotOf(Files.InspectHandle(control), null));
            AddSnapshotEntry(entries, "control/" + RootMembershipRegistry.LedgerName, control,
                RootMembershipRegistry.LedgerName, readBytes: true);
            AddSnapshotEntry(entries, "control/root.lock", control, "root.lock", readBytes: false);
            foreach (var member in Initial.Members)
                AddSnapshotEntry(entries, "control/" + PhysicalStoreLock.GetMemberLockName(SlotFor(member)), control,
                    PhysicalStoreLock.GetMemberLockName(SlotFor(member)), readBytes: false);
        }

        AddParentSnapshot(entries, "target", _parents["target"]);
        AddParentSnapshot(entries, "sibling", _parents["sibling"]);
        return entries;
    }

    private void AddParentSnapshot(SortedDictionary<string, SnapshotEntry> entries,
        string memberId, PhysicalStoreDirectoryHandle parent)
    {
        entries.Add(memberId + "/parent", SnapshotOf(Files.InspectHandle(parent), null));
        foreach (var path in Directory.EnumerateFiles(memberId == "target" ? StateParentPath : SiblingStateParentPath))
        {
            var name = Path.GetFileName(path);
            AddSnapshotEntry(entries, memberId + "/" + name, parent, name, readBytes: true);
        }
    }

    private void AddSnapshotEntry(SortedDictionary<string, SnapshotEntry> entries, string key,
        PhysicalStoreDirectoryHandle parent, string name, bool readBytes)
    {
        var entry = Files.InspectChildNoFollow(parent, name);
        if (entry is null)
            return;
        string? digest = null;
        if (readBytes && entry.Kind == PhysicalStoreEntryKind.RegularFile)
        {
            using var file = Files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
            digest = Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
        }
        entries.Add(key, SnapshotOf(entry, digest));
    }

    private static SnapshotEntry SnapshotOf(PhysicalStoreEntryInfo entry, string? contentDigest)
        => new(entry.Identity, entry.Kind, entry.LinkCount, entry.Length, contentDigest);

    internal string ReadLedgerPayloadDigest()
    {
        using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
        using var ledger = Files.OpenFileChildNoFollow(control, RootMembershipRegistry.LedgerName, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(ledger, RootMembershipRegistry.MaximumStateBytes)));
    }

    internal PhysicalStoreEntryInfo? InspectArtifact(string memberId, string name)
        => Files.InspectChildNoFollow(_parents[memberId], name);

    internal string[] FindTransactionArtifacts(string memberId)
        => Directory.EnumerateFiles(memberId == "target" ? StateParentPath : SiblingStateParentPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(".nuplane-", StringComparison.Ordinal))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var parent in _parents.Values.Reverse())
            parent.Dispose();
        _parents.Clear();
        Root?.Dispose();
        _fixture.Dispose();
    }

    private async Task InitializeAsync(RootMembershipPriorShape priorShape)
    {
        Files = OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        StateParentPath = Path.GetDirectoryName(_fixture.StateFilePath)!;
        SiblingStateFilePath = _fixture.CreateStateSlot("state-sibling/store-state.json");
        SiblingStateParentPath = Path.GetDirectoryName(SiblingStateFilePath)!;
        if (IsAtOrBelow(RootPath, StateParentPath) || IsAtOrBelow(RootPath, SiblingStateParentPath))
            throw new InvalidDataException("Membership test state parents must remain outside the package install root.");

        Root = OwnedProcessDirectory.Open(Files, RootPath);
        _parents.Add("target", OwnedProcessDirectory.Open(Files, StateParentPath));
        _parents.Add("sibling", OwnedProcessDirectory.Open(Files, SiblingStateParentPath));

        var rootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
        var targetParentIdentity = Files.InspectHandle(_parents["target"]).Identity;
        var siblingParentIdentity = Files.InspectHandle(_parents["sibling"]).Identity;
        var targetNameSemantics = CreateNameSemantics(_parents["target"]);
        var siblingNameSemantics = CreateNameSemantics(_parents["sibling"]);
        var targetSlot = new StateSlotIdentity(targetParentIdentity, targetNameSemantics, Path.GetFileName(StateFilePath));
        var siblingSlot = new StateSlotIdentity(siblingParentIdentity, siblingNameSemantics, Path.GetFileName(SiblingStateFilePath));
        TargetSlot = targetSlot;
        SiblingSlot = siblingSlot;

        var targetPrior = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
        RootMemberRecord.MemberBinding targetBinding;
        if (priorShape == RootMembershipPriorShape.Prospective)
        {
            if (File.Exists(StateFilePath))
                throw new IOException("The prospective test state file must remain absent before publication.");
            targetBinding = new RootMemberRecord.ProspectiveBinding(targetParentIdentity, targetNameSemantics, targetSlot.CanonicalBasename);
        }
        else
        {
            if (priorShape == RootMembershipPriorShape.Acknowledged)
                targetPrior = Protect(targetPrior, rootIdentity, 1, "target", 1);
            var targetIdentity = await CreateStateAsync(_parents["target"], targetSlot.CanonicalBasename, targetPrior).ConfigureAwait(false);
            targetBinding = priorShape == RootMembershipPriorShape.ExistingUnprotected
                ? new RootMemberRecord.ExistingUnprotectedBinding(targetSlot, targetIdentity,
                    ProtectionDigest.StateBody(targetPrior), protectionMetadataAbsent: true)
                : new RootMemberRecord.AcknowledgedBinding(targetSlot, targetIdentity, targetPrior.ProtectionRecord!);
        }

        var siblingPrior = Protect(StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch },
            rootIdentity, 1, "sibling", 1);
        var siblingIdentity = await CreateStateAsync(_parents["sibling"], siblingSlot.CanonicalBasename, siblingPrior).ConfigureAwait(false);
        InitialTargetIdentity = targetBinding switch
        {
            RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.ObservedStateFileIdentity,
            RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
            _ => null
        };
        InitialTargetStateDigest = InitialTargetIdentity is null ? null : ReadStateDigestAt(_parents["target"], targetSlot);
        InitialSiblingStateDigest = ReadStateDigestAt(_parents["sibling"], siblingSlot);
        InitialSiblingIdentity = siblingIdentity;
        var members = new[]
        {
            new RootMemberRecord("target", StateFilePath, targetBinding),
            new RootMemberRecord("sibling", SiblingStateFilePath,
                new RootMemberRecord.AcknowledgedBinding(siblingSlot, siblingIdentity, siblingPrior.ProtectionRecord!))
        };
        var seed = new RootMembershipRecord(1, rootIdentity, 1, RootMembershipStatus.Incomplete, members,
            ["target", "sibling"], [], null, ZeroDigest);
        Initial = RootMembershipRegistry.Rebuild(seed, RootMembershipStatus.Incomplete, members, null);
        Registry = new RootMembershipRegistry(Files, new StoreStateSerializer());

        using var control = Files.CreateDirectoryExclusiveAt(Root, RootMembershipRegistry.ControlDirectoryName);
        CreateFile(control, "root.lock", []);
        foreach (var member in Initial.Members)
            CreateFile(control, PhysicalStoreLock.GetMemberLockName(SlotFor(member)), []);
        CreateFile(control, RootMembershipRegistry.LedgerName, new RootMembershipPayloadSerializer().Serialize(Initial));
        AssertSeedReopens();
    }

    private void AssertSeedReopens()
    {
        var actual = Registry.ReadCandidate(Root);
        if (actual.LedgerDigest != Initial.LedgerDigest || actual.Members.Count != 2 ||
            !actual.Members.Select(item => item.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["target", "sibling"]))
        {
            throw new InvalidDataException("The owned membership fixture did not reopen its exact two-member ledger.");
        }
    }

    private PhysicalStoreNameSemantics CreateNameSemantics(PhysicalStoreDirectoryHandle parent)
    {
        var marker = CreateFile(parent, "profile.marker", []);
        return ((IPhysicalStoreNameFileSystem)Files)
            .ObserveCanonicalFileNameNoFollow(parent, "profile.marker", marker).Semantics;
    }

    private async Task<PhysicalFileIdentity> CreateStateAsync(
        PhysicalStoreDirectoryHandle parent, string name, StoreStateRecord state)
    {
        using var payload = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None).ConfigureAwait(false);
        return CreateFile(parent, name, payload.ToArray());
    }

    private PhysicalFileIdentity CreateFile(PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
    {
        using var file = Files.CreateFileExclusiveAt(parent, name);
        Files.WriteNewControlFile(file, bytes);
        return Files.InspectHandle(file).Identity;
    }

    private string ReadStateDigestAt(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot)
    {
        using var file = Files.OpenFileChildNoFollow(parent, slot.CanonicalBasename, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
    }

    private static StoreStateRecord Protect(StoreStateRecord state, PhysicalRootIdentity root,
        long epoch, string memberId, long revision)
    {
        var knownEmpty = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch,
            memberId, revision, ProtectionDigest.StateBody(state), ZeroDigest, knownEmpty, knownEmpty, [], false);
        var protection = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch,
            memberId, revision, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), knownEmpty, knownEmpty, [], false);
        return state with { ProtectionRecord = protection };
    }

    private static StateSlotIdentity SlotFor(RootMemberRecord member) => member.Binding switch
    {
        RootMemberRecord.ProspectiveBinding prospective => new StateSlotIdentity(
            prospective.VerifiedParentIdentity, prospective.NameSemantics, prospective.RequestedBasename),
        RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.StateSlot,
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
        _ => throw new InvalidDataException("The test member binding has no state slot.")
    };

    private static bool IsAtOrBelow(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." ||
               (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    internal sealed record SnapshotEntry(
        PhysicalFileIdentity Identity,
        PhysicalStoreEntryKind Kind,
        ulong LinkCount,
        long Length,
        string? ContentDigest);
}
