using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests.Fixtures;

/// <summary>Owns an absent membership namespace and two external state parents for declaration-process proofs.</summary>
internal sealed class RootMembershipInitializationProcessFixture : IDisposable
{
    private readonly PackageStoreFixture _fixture = new();
    private readonly Dictionary<string, PhysicalStoreDirectoryHandle> _parents = new(StringComparer.Ordinal);
    private bool _disposed;

    private RootMembershipInitializationProcessFixture() { }

    internal IPhysicalStoreFileSystem Files { get; private set; } = null!;
    internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
    internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
    internal string RootPath => _fixture.PackageInstallRoot;
    internal IReadOnlyList<RootMemberRecord> DeclaredMembers { get; private set; } = [];
    internal IReadOnlyDictionary<string, string> StateParentPaths { get; private set; } = null!;

    internal static async Task<RootMembershipInitializationProcessFixture> CreateAsync()
    {
        var fixture = new RootMembershipInitializationProcessFixture();
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

    internal string WriteRequest(string operationId, string? checkpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new
        {
            operationId,
            rootPath = RootPath,
            enrollmentEpoch = 23,
            quiescentCutoverConfirmed = true,
            declaredMembers = DeclaredMembers.Select(member => new
            {
                memberId = member.MemberId,
                configuredLocator = member.ConfiguredLocator
            }).ToArray(),
            checkpoint
        };
        var path = _fixture.GetPath($"process-requests/{operationId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(request,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return path;
    }

    internal RootMembershipRegistry NewRegistry()
        => new(Files, new StoreStateSerializer());

    internal bool ControlDirectoryExists()
        => Files.InspectChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName) is not null;

    internal string[] EnrollmentStageNames()
        => Directory.EnumerateDirectories(RootPath)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(RootMembershipRegistry.EnrollmentStagePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => name!)
            .ToArray();

    internal string[] DirectoryEntryNames(string directoryName)
        => Directory.EnumerateFileSystemEntries(Path.Combine(RootPath, directoryName))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => name!)
            .ToArray();

    internal RootMembershipRecord ReadLedgerFromDirectory(string directoryName)
    {
        using var directory = Files.OpenDirectoryChildNoFollow(Root, directoryName);
        using var payload = Files.OpenFileChildNoFollow(directory, RootMembershipRegistry.LedgerName, FileAccess.Read);
        return new Nuplane.Store.Coordination.MembershipSerialization.RootMembershipPayloadSerializer()
            .Deserialize(Files.ReadControlFile(payload, RootMembershipRegistry.MaximumStateBytes));
    }

    internal byte[] ReadRootLock(string directoryName)
    {
        using var directory = Files.OpenDirectoryChildNoFollow(Root, directoryName);
        using var file = Files.OpenFileChildNoFollow(directory, "root.lock", FileAccess.Read);
        return Files.ReadControlFile(file, maximumBytes: 1);
    }

    internal SortedDictionary<string, SnapshotEntry> CaptureExternalStateSnapshot()
    {
        var result = new SortedDictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        foreach (var pair in _parents)
        {
            result.Add(pair.Key + "/parent", SnapshotOf(Files.InspectHandle(pair.Value), null));
            foreach (var path in Directory.EnumerateFileSystemEntries(StateParentPaths[pair.Key]))
            {
                var name = Path.GetFileName(path);
                var entry = Files.InspectChildNoFollow(pair.Value, name)
                    ?? throw new InvalidDataException("An external state entry disappeared during snapshotting.");
                string? digest = null;
                if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                {
                    using var file = Files.OpenFileChildNoFollow(pair.Value, name, FileAccess.Read);
                    digest = Convert.ToHexString(SHA256.HashData(
                        Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
                }
                result.Add(pair.Key + "/" + name, SnapshotOf(entry, digest));
            }
        }

        return result;
    }

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

    private async Task InitializeAsync()
    {
        Files = OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        var siblingStatePath = _fixture.CreateStateSlot("state-sibling/store-state.json");
        var statePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary"] = _fixture.StateFilePath,
            ["sibling"] = siblingStatePath
        };
        var parentPaths = statePaths.ToDictionary(pair => pair.Key, pair => Path.GetDirectoryName(pair.Value)!, StringComparer.Ordinal);
        if (IsAtOrBelow(RootPath, parentPaths["primary"]) || IsAtOrBelow(RootPath, parentPaths["sibling"]))
            throw new InvalidDataException("Initial declaration test state parents must remain outside the package root.");

        Root = OwnedProcessDirectory.Open(Files, RootPath);
        foreach (var pair in parentPaths)
            _parents.Add(pair.Key, OwnedProcessDirectory.Open(Files, pair.Value));
        RootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
        StateParentPaths = parentPaths;

        foreach (var pair in statePaths)
        {
            var state = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
            using var payload = new MemoryStream();
            await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None).ConfigureAwait(false);
            CreateFile(_parents[pair.Key], Path.GetFileName(pair.Value), payload.ToArray());
        }

        DeclaredMembers = statePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
            new RootMemberRecord.DeclaredBinding())).ToArray();
    }

    private PhysicalFileIdentity CreateFile(PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
    {
        using var file = Files.CreateFileExclusiveAt(parent, name);
        Files.WriteNewControlFile(file, bytes);
        return Files.InspectHandle(file).Identity;
    }

    private static SnapshotEntry SnapshotOf(PhysicalStoreEntryInfo entry, string? contentDigest)
        => new(entry.Identity, entry.Kind, entry.LinkCount, entry.Length, contentDigest);

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
