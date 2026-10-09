using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class RootMembershipLocatorReplayTests
{
    private const long EnrollmentEpoch = 41;

    [SupportedPhysicalStoreFact]
    public async Task WithQuiescentIncompleteMemberLocationsAsync_ReplaysExternalAndSameRootProspectiveSlotsWithoutPayloadReads()
    {
        using var context = new Context();
        var existingPath = context.Fixture.StateFilePath;
        var existingBytes = "opaque-state-payload"u8.ToArray();
        File.WriteAllBytes(existingPath, existingBytes);
        var prospectivePath = context.Fixture.CreateStateSlot("packages/nested/prospective.json");
        var declarations = new[]
        {
            new RootMemberRecord("existing", existingPath, new RootMemberRecord.DeclaredBinding()),
            new RootMemberRecord("prospective", prospectivePath, new RootMemberRecord.DeclaredBinding())
        };
        context.InitializeIncomplete(declarations);
        var priorExternalEntries = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(existingPath)!)
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        ResolvedMemberStateLocation? escapedLocation = null;
        StateSlotIdentity? prospectiveSlot = null;

        var resultDigest = await context.Registry.WithQuiescentIncompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch, quiescentCutoverConfirmed: true,
            (locked, _) =>
            {
                Assert.Equal(RootMembershipStatus.Incomplete, locked.Ledger.Status);
                Assert.Null(locked.Ledger.PendingStateCommit);
                Assert.Equal(new[] { "existing", "prospective" }, locked.Ledger.Members.Select(member => member.MemberId));
                Assert.Equal(2, locked.Locations.Count);
                Assert.NotNull(locked.Locations["existing"].ExistingFileIdentity);
                Assert.Equal(Path.GetFileName(existingPath), locked.Locations["existing"].Slot.CanonicalBasename);
                Assert.Null(locked.Locations["prospective"].ExistingFileIdentity);
                Assert.Equal(Path.GetFileName(prospectivePath), locked.Locations["prospective"].Slot.CanonicalBasename);
                Assert.Equal(context.ParentIdentity(prospectivePath),
                    context.Files.InspectHandle(locked.Locations["prospective"].Parent).Identity);
                context.AssertLocksHeld(locked.Locations.Values.Select(location => location.Slot));
                Assert.Equal("root.lock", context.Files.SuccessfulLockNames[0]);
                Assert.Equal(3, context.Files.SuccessfulLockNames.Length);
                locked.Revalidate();
                escapedLocation = locked.Locations["existing"];
                prospectiveSlot = locked.Locations["prospective"].Slot;
                return Task.FromResult(locked.Ledger.LedgerDigest);
            }, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(resultDigest));
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        Assert.Equal(existingBytes, File.ReadAllBytes(existingPath));
        Assert.Equal(priorExternalEntries,
            Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(existingPath)!).Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.Throws<PackageStoreAdmissionException>(() => _ = escapedLocation!.Parent);
        var controlEntries = context.ControlEntries();
        Assert.Contains("root.lock", controlEntries);
        Assert.Contains(PhysicalStoreLock.GetMemberLockName(context.ObserveSlot(existingPath)), controlEntries);
        Assert.Contains(PhysicalStoreLock.GetMemberLockName(prospectiveSlot!), controlEntries);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithQuiescentIncompleteMemberLocationsAsync_DeclaredMemberStateReadRefusesBeforePayloadRead()
    {
        using var context = new Context();
        var statePath = context.Fixture.StateFilePath;
        File.WriteAllBytes(statePath, "opaque-state-payload"u8.ToArray());
        context.InitializeIncomplete([new RootMemberRecord("member", statePath, new RootMemberRecord.DeclaredBinding())]);

        await context.Registry.WithQuiescentIncompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch, quiescentCutoverConfirmed: true,
            async (locked, token) =>
            {
                await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => locked.ReadMemberStateAsync("member", token));
                return locked.Ledger.LedgerDigest;
            }, CancellationToken.None);

        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_ReplaysParentAliasAndUsesOnlyExistingBoundLocks()
    {
        using var context = new Context();
        var aliasDirectory = Path.Combine(context.Fixture.RootPath, "state-parent-alias");
        Directory.CreateSymbolicLink(aliasDirectory, Path.GetDirectoryName(context.Fixture.StateFilePath)!);
        var aliasedLocator = Path.Combine(aliasDirectory, Path.GetFileName(context.Fixture.StateFilePath)!);
        var (member, slot, fileIdentity) = await context.InitializeComplete(context.Fixture.StateFilePath, aliasedLocator);
        var controlBefore = context.ControlEntries();
        var callbackCount = 0;

        var digest = await context.Registry.WithCompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch,
            (locked, _) =>
            {
                callbackCount++;
                var location = Assert.Single(locked.Locations).Value;
                Assert.Equal(RootMembershipStatus.Complete, locked.Ledger.Status);
                Assert.Equal(member.MemberId, Assert.Single(locked.Locations).Key);
                Assert.Equal(slot, location.Slot);
                Assert.Equal(fileIdentity, location.ExistingFileIdentity);
                Assert.Equal(PhysicalStoreEntryKind.Directory, context.Files.InspectHandle(location.Parent).Kind);
                context.AssertLocksHeld([slot]);
                Assert.Equal(new[] { "root.lock", PhysicalStoreLock.GetMemberLockName(slot) }, context.Files.SuccessfulLockNames);
                locked.Revalidate();
                return Task.FromResult(locked.Ledger.LedgerDigest);
            }, CancellationToken.None);

        Assert.Equal(1, callbackCount);
        Assert.False(string.IsNullOrWhiteSpace(digest));
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        Assert.Equal(controlBefore, context.ControlEntries());
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_ReadsExactBoundStateOnlyInsideTheCallback()
    {
        using var context = new Context();
        var (member, _, _) = await context.InitializeComplete(context.Fixture.StateFilePath, context.Fixture.StateFilePath);
        var registry = new RootMembershipRegistry(context.Files, new StoreStateSerializer());

        var digest = await registry.WithCompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch,
            async (locked, token) =>
            {
                var state = await locked.ReadMemberStateAsync(member.MemberId, token);
                Assert.NotNull(state?.ProtectionRecord);
                locked.Revalidate();
                return state!.ProtectionRecord!.ProtectionDigest;
            }, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(digest));
        Assert.Equal(1, context.Files.MemberPayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_RefusesFinalFileLinksAndHardlinksBeforeCallback()
    {
        foreach (var hardLink in new[] { false, true })
        {
            using var context = new Context();
            var ordinaryPath = context.Fixture.StateFilePath;
            var (member, _, _) = await context.InitializeComplete(ordinaryPath, ordinaryPath);
            var linkedPath = Path.Combine(Path.GetDirectoryName(ordinaryPath)!, hardLink ? "hardlink-state.json" : "symlink-state.json");
            if (hardLink)
                CreateHardLink(ordinaryPath, linkedPath);
            else
                File.CreateSymbolicLink(linkedPath, ordinaryPath);
            context.ReplaceMemberLocator(member.MemberId, linkedPath);
            var callbackCount = 0;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithCompleteMemberLocationsAsync(
                context.Root, context.RootIdentity, EnrollmentEpoch,
                (locked, _) =>
                {
                    callbackCount++;
                    return Task.FromResult(locked.Ledger.LedgerDigest);
                }, CancellationToken.None));

            Assert.Equal(0, callbackCount);
            Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_ChangedLedgerAndStateSlotRefuseBeforeCallback()
    {
        using (var changedLedger = new Context())
        {
            var (member, _, _) = await changedLedger.InitializeComplete(
                changedLedger.Fixture.StateFilePath, changedLedger.Fixture.StateFilePath);
            var mutationFired = false;
            changedLedger.Files.AfterRootLockAcquired = () =>
            {
                mutationFired = true;
                changedLedger.ReplaceMemberLocator(member.MemberId,
                    Path.Combine(changedLedger.Fixture.RootPath, "changed", "state.json"));
            };
            var callbackCount = 0;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => changedLedger.Registry.WithCompleteMemberLocationsAsync(
                changedLedger.Root, changedLedger.RootIdentity, EnrollmentEpoch,
                (locked, _) =>
                {
                    callbackCount++;
                    return Task.FromResult(locked.Ledger.LedgerDigest);
                }, CancellationToken.None));

            Assert.Equal(0, callbackCount);
            Assert.True(mutationFired);
            Assert.Equal(0, changedLedger.StateSerializer.ReadCount);
            Assert.Equal(0, changedLedger.Files.MemberPayloadReadCount);
        }

        using (var changedSlot = new Context())
        {
            var statePath = changedSlot.Fixture.StateFilePath;
            var (member, _, _) = await changedSlot.InitializeComplete(statePath, statePath);
            var mutationFired = false;
            changedSlot.Files.AfterInspectChild = (parent, name, entry) =>
            {
                if (entry is null || name != Path.GetFileName(statePath))
                    return;
                var parentIdentity = changedSlot.Files.InspectHandle(parent).Identity;
                if (parentIdentity != changedSlot.ExpectedStateParentIdentity)
                    return;
                mutationFired = true;
                changedSlot.Files.AfterInspectChild = null;
                var replacement = statePath + ".replacement";
                File.WriteAllBytes(replacement, "replacement-state-payload"u8.ToArray());
                File.Move(replacement, statePath, overwrite: true);
            };
            var callbackCount = 0;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => changedSlot.Registry.WithCompleteMemberLocationsAsync(
                changedSlot.Root, changedSlot.RootIdentity, EnrollmentEpoch,
                (locked, _) =>
                {
                    callbackCount++;
                    return Task.FromResult(locked.Ledger.LedgerDigest);
                }, CancellationToken.None));

            Assert.Equal(0, callbackCount);
            Assert.True(mutationFired);
            Assert.Equal(0, changedSlot.StateSerializer.ReadCount);
            Assert.Equal(0, changedSlot.Files.MemberPayloadReadCount);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WithQuiescentIncompleteMemberLocationsAsync_RequiresQuiescenceAndAbsoluteHints()
    {
        using var context = new Context();
        var declared = new RootMemberRecord("member", "relative-state.json", new RootMemberRecord.DeclaredBinding());
        context.InitializeIncomplete([declared]);
        var callbackCount = 0;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithQuiescentIncompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch, quiescentCutoverConfirmed: false,
            (locked, _) =>
            {
                callbackCount++;
                return Task.FromResult(locked.Ledger.LedgerDigest);
            }, CancellationToken.None));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithQuiescentIncompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch, quiescentCutoverConfirmed: true,
            (locked, _) =>
            {
                callbackCount++;
                return Task.FromResult(locked.Ledger.LedgerDigest);
            }, CancellationToken.None));

        Assert.Equal(0, callbackCount);
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task Revalidate_ExternalMemberLocatorRejectsLedgerChangedInsideCallback()
    {
        foreach (var replaceWithIdenticalBytes in new[] { false, true })
        {
            using var context = new Context();
            var (member, _, _) = await context.InitializeComplete(context.Fixture.StateFilePath, context.Fixture.StateFilePath);
            var callbackCount = 0;
            await context.Registry.WithCompleteMemberLocationsAsync(
                context.Root, context.RootIdentity, EnrollmentEpoch,
                (locked, _) =>
                {
                    callbackCount++;
                    locked.Revalidate();
                    if (replaceWithIdenticalBytes)
                        context.ReplaceLedger(context.Registry.ReadCandidate(context.Root));
                    else
                        context.ReplaceMemberLocator(member.MemberId,
                            Path.Combine(context.Fixture.RootPath, "changed-parent", "state.json"));
                    Assert.Throws<PackageStoreAdmissionException>(locked.Revalidate);
                    return Task.FromResult(true);
                }, CancellationToken.None);
            Assert.Equal(1, callbackCount);
            Assert.Equal(0, context.StateSerializer.ReadCount);
            Assert.Equal(0, context.Files.MemberPayloadReadCount);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_MissingBoundLockIsRefusedWithoutRecreation()
    {
        using var context = new Context();
        var (_, slot, _) = await context.InitializeComplete(context.Fixture.StateFilePath, context.Fixture.StateFilePath);
        var lockPath = Path.Combine(context.Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
            PhysicalStoreLock.GetMemberLockName(slot));
        File.Delete(lockPath);
        var controlBefore = context.ControlEntries();
        var callbackCount = 0;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithCompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch,
            (locked, _) =>
            {
                callbackCount++;
                return Task.FromResult(locked.Ledger);
            }, CancellationToken.None));
        Assert.Equal(0, callbackCount);
        Assert.False(File.Exists(lockPath));
        Assert.Equal(controlBefore, context.ControlEntries());
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithQuiescentIncompleteMemberLocationsAsync_DuplicateNativeSlotsRefuseBeforeMemberLockProvisioning()
    {
        using var context = new Context();
        File.WriteAllBytes(context.Fixture.StateFilePath, "opaque-state"u8.ToArray());
        context.InitializeIncomplete([
            new RootMemberRecord("first", context.Fixture.StateFilePath, new RootMemberRecord.DeclaredBinding()),
            new RootMemberRecord("second", context.Fixture.StateFilePath, new RootMemberRecord.DeclaredBinding())]);
        var controlBefore = context.ControlEntries();
        var callbackCount = 0;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithQuiescentIncompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch, true,
            (locked, _) =>
            {
                callbackCount++;
                return Task.FromResult(locked.Ledger);
            }, CancellationToken.None));
        Assert.Equal(0, callbackCount);
        Assert.Equal(controlBefore, context.ControlEntries());
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithCompleteMemberLocationsAsync_ParentAliasThroughDifferentAuthorityRefusesBeforeCallback()
    {
        using var context = new Context();
        var (member, _, _) = await context.InitializeComplete(context.Fixture.StateFilePath, context.Fixture.StateFilePath);
        var otherPath = context.Fixture.CreateDirectory("other-packages");
        using var otherRoot = PhysicalStoreTestDirectory.Open(context.Files, otherPath);
        var otherIdentity = new PhysicalRootIdentity(context.Files.InspectHandle(otherRoot).Identity);
        context.Registry.InitializeIncomplete(otherRoot, otherIdentity, 1,
            [new RootMemberRecord("other", context.Fixture.StateFilePath, new RootMemberRecord.DeclaredBinding())],
            quiescentCutoverConfirmed: true, CancellationToken.None);
        var alias = Path.Combine(context.Fixture.RootPath, "other-parent-alias");
        Directory.CreateSymbolicLink(alias, otherPath);
        context.ReplaceMemberLocator(member.MemberId, Path.Combine(alias, "state.json"));
        var callbackCount = 0;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.WithCompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, EnrollmentEpoch,
            (locked, _) =>
            {
                callbackCount++;
                return Task.FromResult(locked.Ledger);
            }, CancellationToken.None));
        Assert.Equal(0, callbackCount);
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
    }

    private sealed class Context : IDisposable
    {
        private readonly IPhysicalStoreFileSystem _nativeFiles;

        internal Context()
        {
            Fixture = new PackageStoreFixture();
            _nativeFiles = OperatingSystem.IsWindows()
                ? new WindowsPhysicalStoreFileSystem()
                : new UnixPhysicalStoreFileSystem();
            Files = new MutatingFileSystem(_nativeFiles);
            StateSerializer = new NoPayloadReadSerializer();
            Registry = new RootMembershipRegistry(Files, StateSerializer);
            Root = PhysicalStoreTestDirectory.Open(_nativeFiles, Fixture.PackageInstallRoot);
            RootIdentity = new PhysicalRootIdentity(_nativeFiles.InspectHandle(Root).Identity);
            using var stateParent = PhysicalStoreTestDirectory.Open(_nativeFiles, Path.GetDirectoryName(Fixture.StateFilePath)!);
            ExpectedStateParentIdentity = _nativeFiles.InspectHandle(stateParent).Identity;
        }

        internal PackageStoreFixture Fixture { get; }
        internal MutatingFileSystem Files { get; }
        internal NoPayloadReadSerializer StateSerializer { get; }
        internal RootMembershipRegistry Registry { get; }
        internal PhysicalStoreDirectoryHandle Root { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal PhysicalFileIdentity ExpectedStateParentIdentity { get; }

        internal void InitializeIncomplete(IReadOnlyList<RootMemberRecord> members)
            => Registry.InitializeIncomplete(Root, RootIdentity, EnrollmentEpoch, members,
                quiescentCutoverConfirmed: true, CancellationToken.None);

        internal async Task<(RootMemberRecord Member, StateSlotIdentity Slot, PhysicalFileIdentity FileIdentity)>
            InitializeComplete(string stateFilePath, string configuredLocator)
        {
            var body = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
            var stateBytes = await SerializeState(body);
            File.WriteAllBytes(stateFilePath, stateBytes);
            using var stateParent = PhysicalStoreTestDirectory.Open(_nativeFiles, Path.GetDirectoryName(stateFilePath)!);
            var stateObservation = new PhysicalStoreIdentity(_nativeFiles).ObserveStateSlot(
                stateParent, Path.GetFileName(stateFilePath)!);
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
            var protectionCandidate = new PackageProtectionRecord(1, RootIdentity, EnrollmentEpoch, "member", 1,
                ProtectionDigest.StateBody(body), new string('0', 64), closure, closure, [], false);
            var protection = new PackageProtectionRecord(1, RootIdentity, EnrollmentEpoch, "member", 1,
                protectionCandidate.StateBodyDigest, ProtectionDigest.Protection(protectionCandidate),
                closure, closure, [], false);
            var protectedState = body with { ProtectionRecord = protection };
            File.WriteAllBytes(stateFilePath, await SerializeState(protectedState));
            stateObservation = new PhysicalStoreIdentity(_nativeFiles).ObserveStateSlot(
                stateParent, Path.GetFileName(stateFilePath)!);

            var member = new RootMemberRecord("member", configuredLocator,
                new RootMemberRecord.AcknowledgedBinding(stateObservation.Slot, stateObservation.FileIdentity, protection));
            var candidate = new RootMembershipRecord(1, RootIdentity, EnrollmentEpoch, RootMembershipStatus.Complete,
                [member], [member.MemberId], [], null, new string('0', 64));
            var ledger = RootMembershipRegistry.Rebuild(candidate, RootMembershipStatus.Complete, [member], null);
            using var control = _nativeFiles.CreateDirectoryExclusiveAt(Root, RootMembershipRegistry.ControlDirectoryName);
            CreateControlFile(control, "root.lock", []);
            CreateControlFile(control, PhysicalStoreLock.GetMemberLockName(stateObservation.Slot), []);
            CreateControlFile(control, RootMembershipRegistry.LedgerName, new RootMembershipPayloadSerializer().Serialize(ledger));
            return (member, stateObservation.Slot, stateObservation.FileIdentity);
        }

        internal StateSlotIdentity ObserveSlot(string path)
        {
            using var parent = PhysicalStoreTestDirectory.Open(_nativeFiles, Path.GetDirectoryName(path)!);
            return new PhysicalStoreIdentity(_nativeFiles).ObserveStateSlot(parent, Path.GetFileName(path)!).Slot;
        }

        internal PhysicalFileIdentity ParentIdentity(string path)
        {
            using var parent = PhysicalStoreTestDirectory.Open(_nativeFiles, Path.GetDirectoryName(path)!);
            return _nativeFiles.InspectHandle(parent).Identity;
        }

        internal void AssertLocksHeld(IEnumerable<StateSlotIdentity> slots)
            => RootMembershipLocatorReplayTests.AssertLocksHeld(_nativeFiles, Root, slots);

        internal string[] ControlEntries()
        {
            using var control = _nativeFiles.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            return Directory.EnumerateFileSystemEntries(Path.Combine(Fixture.PackageInstallRoot,
                    RootMembershipRegistry.ControlDirectoryName))
                .Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        internal void ReplaceMemberLocator(string memberId, string locator)
        {
            var ledger = Registry.ReadCandidate(Root);
            var members = ledger.Members.Select(member => string.Equals(member.MemberId, memberId, StringComparison.Ordinal)
                ? new RootMemberRecord(member.MemberId, locator, member.Binding)
                : member).ToArray();
            var changed = RootMembershipRegistry.Rebuild(ledger, ledger.Status, members, ledger.PendingStateCommit);
            ReplaceLedger(changed);
        }

        internal void ReplaceLedger(RootMembershipRecord ledger)
        {
            var path = Path.Combine(Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
                RootMembershipRegistry.LedgerName);
            var temporary = path + ".replacement";
            File.WriteAllBytes(temporary, new RootMembershipPayloadSerializer().Serialize(ledger));
            File.Move(temporary, path, overwrite: true);
        }

        public void Dispose()
        {
            try { Root.Dispose(); }
            finally { Fixture.Dispose(); }
        }

        private static async Task<byte[]> SerializeState(StoreStateRecord state)
        {
            using var buffer = new MemoryStream();
            await new StoreStateSerializer().WritePayloadAsync(buffer, state, CancellationToken.None);
            return buffer.ToArray();
        }

        private void CreateControlFile(PhysicalStoreDirectoryHandle control, string name, byte[] bytes)
        {
            using var file = _nativeFiles.CreateFileExclusiveAt(control, name);
            _nativeFiles.WriteNewControlFile(file, bytes);
        }
    }

    private sealed class NoPayloadReadSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private int _readCount;

        internal int ReadCount => Volatile.Read(ref _readCount);

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Locator replay must not reopen a member-state path.");

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Locator replay must not write a member-state path.");

        public Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            throw new InvalidOperationException("Locator replay must not read member-state payload bytes.");
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private sealed class MutatingFileSystem : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem, IPhysicalStoreDirectoryPublicationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _inner;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStorePublicationFileSystem _publication;
        private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryPublication;
        private readonly ConcurrentDictionary<PhysicalFileIdentity, string> _openedNames = new();
        private readonly ConcurrentQueue<string> _successfulLockNames = new();
        private int _memberPayloadReadCount;

        internal MutatingFileSystem(IPhysicalStoreFileSystem inner)
        {
            _inner = inner;
            _names = inner as IPhysicalStoreNameFileSystem ?? throw new InvalidOperationException();
            _publication = inner as IPhysicalStorePublicationFileSystem ?? throw new InvalidOperationException();
            _directoryPublication = inner as IPhysicalStoreDirectoryPublicationFileSystem ?? throw new InvalidOperationException();
        }

        internal Action? AfterRootLockAcquired { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, PhysicalStoreEntryInfo?>? AfterInspectChild { get; set; }
        internal string[] SuccessfulLockNames => _successfulLockNames.ToArray();
        internal int MemberPayloadReadCount => Volatile.Read(ref _memberPayloadReadCount);

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _inner.OpenNamespaceRoot(anchor);

        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var entry = _inner.InspectChildNoFollow(parent, singleName);
            AfterInspectChild?.Invoke(parent, singleName, entry);
            return entry;
        }

        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.OpenDirectoryChildNoFollow(parent, singleName);

        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => _inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            var file = _inner.OpenFileChildNoFollow(parent, singleName, access);
            _openedNames[_inner.InspectHandle(file).Identity] = singleName;
            return file;
        }

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedLinkIdentity)
            => _inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);

        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => _inner.InspectHandle(handle);

        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _inner.CreateDirectoryExclusiveAt(parent, singleName);

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var file = _inner.CreateFileExclusiveAt(parent, singleName);
            _openedNames[_inner.InspectHandle(file).Identity] = singleName;
            return file;
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            var identity = _inner.InspectHandle(file).Identity;
            if (_openedNames.TryGetValue(identity, out var name) && name.EndsWith(".json", StringComparison.Ordinal) &&
                name != RootMembershipRegistry.LedgerName)
            {
                Interlocked.Increment(ref _memberPayloadReadCount);
            }
            return _inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => _inner.WriteNewControlFile(file, contents);

        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var identity = _inner.InspectHandle(file).Identity;
            var acquired = await _inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (acquired is not null && _openedNames.TryGetValue(identity, out var name))
            {
                _successfulLockNames.Enqueue(name);
                if (name == "root.lock")
                {
                    var callback = AfterRootLockAcquired;
                    AfterRootLockAcquired = null;
                    try
                    {
                        callback?.Invoke();
                    }
                    catch
                    {
                        await acquired.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            }
            return acquired;
        }

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent,
            string singleName, PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);

        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);

        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);

        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent,
            string singleName, PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryPublication.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);

        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => _directoryPublication.PublishDirectoryNoReplaceAt(parent, stagedName, expectedStagedIdentity, destinationName);
    }

    private static void AssertLocksHeld(IPhysicalStoreFileSystem files, PhysicalStoreDirectoryHandle root,
        IEnumerable<StateSlotIdentity> slots)
    {
        using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        AssertLockHeld(control, "root.lock");
        foreach (var slot in slots)
            AssertLockHeld(control, PhysicalStoreLock.GetMemberLockName(slot));

        void AssertLockHeld(PhysicalStoreDirectoryHandle parent, string lockName)
        {
            using var file = files.OpenFileChildNoFollow(parent, lockName, FileAccess.ReadWrite);
            var attempt = files.TryAcquireExclusiveLock(file).AsTask().GetAwaiter().GetResult();
            if (attempt is null)
                return;
            attempt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Fail($"The callback did not retain native lock '{lockName}'.");
        }
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : LinkUnix(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {result}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkDarwin(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkLinux(string existingPath, string newPath);

    private static int LinkUnix(string existingPath, string newPath)
        => OperatingSystem.IsMacOS() ? LinkDarwin(existingPath, newPath) : LinkLinux(existingPath, newPath);
}
