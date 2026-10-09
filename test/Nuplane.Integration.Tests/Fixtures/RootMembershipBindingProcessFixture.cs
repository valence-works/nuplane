using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests.Fixtures;

/// <summary>Owns a declared membership ledger with two external existing states and one absent slot.</summary>
internal sealed class RootMembershipBindingProcessFixture : IDisposable
{
    internal const long EnrollmentEpoch = 41;
    private readonly PackageStoreFixture _fixture = new();
    private readonly Dictionary<string, PhysicalStoreDirectoryHandle> _parents = new(StringComparer.Ordinal);
    private bool _disposed;

    private RootMembershipBindingProcessFixture() { }

    internal IPhysicalStoreFileSystem Files { get; private set; } = null!;
    internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
    internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
    internal string RootPath => _fixture.PackageInstallRoot;
    internal IReadOnlyList<RootMemberRecord> Declarations { get; private set; } = [];
    internal IReadOnlyDictionary<string, BindingLocation> Locations { get; private set; } = null!;

    internal PhysicalStoreDirectoryHandle GetParent(string memberId)
        => _parents.TryGetValue(memberId, out var parent)
            ? parent
            : throw new KeyNotFoundException($"No held state parent exists for member '{memberId}'.");

    internal static async Task<RootMembershipBindingProcessFixture> CreateAsync()
    {
        var fixture = new RootMembershipBindingProcessFixture();
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

    internal RootMembershipRegistry NewRegistry()
        => new(Files, new StoreStateSerializer());

    internal string WriteRequest(string operationId, string? checkpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new
        {
            operationId,
            rootPath = RootPath,
            enrollmentEpoch = EnrollmentEpoch,
            quiescentCutoverConfirmed = true,
            declaredMembers = Declarations.Select(member => new
            {
                memberId = member.MemberId,
                configuredLocator = member.ConfiguredLocator
            }).ToArray(),
            memberLocations = Locations.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    parentPath = pair.Value.ParentPath,
                    requestedBasename = pair.Value.RequestedBasename
                },
                StringComparer.Ordinal),
            checkpoint
        };
        var path = _fixture.GetPath($"process-requests/{operationId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(request,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return path;
    }

    internal SortedDictionary<string, StateParentEntrySnapshot> CaptureExternalStateSnapshot()
    {
        var result = new SortedDictionary<string, StateParentEntrySnapshot>(StringComparer.Ordinal);
        foreach (var pair in _parents)
        {
            result.Add(pair.Key + "/parent", Snapshot(Files.InspectHandle(pair.Value), null));
            foreach (var path in Directory.EnumerateFileSystemEntries(Locations[pair.Key].ParentPath))
            {
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException("An external state entry has no basename.");
                var entry = Files.InspectChildNoFollow(pair.Value, name)
                    ?? throw new InvalidDataException("An external state entry disappeared while capturing its snapshot.");
                string? digest = null;
                if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                {
                    using var file = Files.OpenFileChildNoFollow(pair.Value, name, FileAccess.Read);
                    digest = Convert.ToHexString(SHA256.HashData(
                        Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
                }

                result.Add(pair.Key + "/" + name, Snapshot(entry, digest));
            }
        }

        return result;
    }

    internal IReadOnlyList<StateSlotIdentity> ObserveAllSlots()
    {
        var identity = new PhysicalStoreIdentity(Files);
        var names = (IPhysicalStoreNameFileSystem)Files;
        return Declarations.Select(member =>
        {
            var location = Locations[member.MemberId];
            var parent = _parents[member.MemberId];
            if (Files.InspectChildNoFollow(parent, location.RequestedBasename) is not null)
                return identity.ObserveStateSlot(parent, location.RequestedBasename).Slot;

            return new StateSlotIdentity(
                Files.InspectHandle(parent).Identity,
                names.ObserveDirectoryNameSemantics(parent),
                location.RequestedBasename);
        }).ToArray();
    }

    internal async Task<string> ReadStateBodyDigestAsync(string memberId, CancellationToken cancellationToken)
    {
        var location = Locations[memberId];
        var parent = _parents[memberId];
        using var file = Files.OpenFileChildNoFollow(parent, location.RequestedBasename, FileAccess.Read);
        using var payload = new MemoryStream(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes));
        var state = await new StoreStateSerializer().ReadPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
        if (state.ProtectionRecord is not null)
            throw new InvalidDataException("The binding fixture state unexpectedly contains protection metadata.");
        return ProtectionDigest.StateBody(state);
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
        var statePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["existing-a"] = _fixture.StateFilePath,
            ["existing-b"] = _fixture.CreateStateSlot("state-sibling/store-state.json"),
            ["prospective"] = _fixture.CreateStateSlot("state-prospective/store-state.json")
        };
        var parentPaths = statePaths.ToDictionary(pair => pair.Key,
            pair => Path.GetDirectoryName(pair.Value)!, StringComparer.Ordinal);
        if (parentPaths.Values.Any(path => IsAtOrBelow(RootPath, path)))
            throw new InvalidDataException("Binding fixture state parents must remain outside the package install root.");

        Root = OwnedProcessDirectory.Open(Files, RootPath);
        foreach (var pair in parentPaths)
            _parents.Add(pair.Key, OwnedProcessDirectory.Open(Files, pair.Value));
        RootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
        Locations = statePaths.ToDictionary(pair => pair.Key,
            pair => new BindingLocation(parentPaths[pair.Key], Path.GetFileName(pair.Value)), StringComparer.Ordinal);

        foreach (var memberId in new[] { "existing-a", "existing-b" })
        {
            var state = StoreStateRecord.Empty() with
            {
                UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(memberId == "existing-a" ? 1 : 2)
            };
            using var payload = new MemoryStream();
            await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None).ConfigureAwait(false);
            using var file = Files.CreateFileExclusiveAt(_parents[memberId], Locations[memberId].RequestedBasename);
            Files.WriteNewControlFile(file, payload.ToArray());
        }

        if (Files.InspectChildNoFollow(_parents["prospective"], Locations["prospective"].RequestedBasename) is not null)
            throw new InvalidDataException("The prospective binding fixture slot must remain absent.");

        Declarations = statePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
            new RootMemberRecord.DeclaredBinding())).ToArray();
        var registry = NewRegistry();
        var declared = registry.InitializeIncomplete(Root, RootIdentity, EnrollmentEpoch, Declarations,
            quiescentCutoverConfirmed: true, cancellationToken: CancellationToken.None);
        if (declared.Status != RootMembershipStatus.Incomplete ||
            !declared.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(Locations.Keys))
        {
            throw new InvalidDataException("The binding fixture did not publish its exact all-Declared ledger.");
        }
    }

    private static StateParentEntrySnapshot Snapshot(PhysicalStoreEntryInfo info, string? digest)
        => new(info.Identity, info.Kind, info.LinkCount, info.Length, digest);

    private static bool IsAtOrBelow(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." ||
               (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    internal sealed record BindingLocation(string ParentPath, string RequestedBasename);

    internal sealed record StateParentEntrySnapshot(
        PhysicalFileIdentity Identity,
        PhysicalStoreEntryKind Kind,
        ulong LinkCount,
        long Length,
        string? ContentDigest);
}
