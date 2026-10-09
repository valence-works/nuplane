using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class RootMembershipInitializationTests
{
    private const long EnrollmentEpoch = 7;

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_PublishesEveryDeclaredTargetAndRootLockBeforeMemberStateIo()
    {
        using var context = InitializationContext.Create(memberCount: 3);

        var result = context.Registry.InitializeIncomplete(
            context.Root,
            context.RootIdentity,
            EnrollmentEpoch,
            context.DeclaredMembers,
            quiescentCutoverConfirmed: true,
            CancellationToken.None);

        AssertDeclaredIncomplete(context, result);
        Assert.Equal(EnrollmentEpoch, result.EnrollmentEpoch);
        Assert.Equal(result.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        Assert.Empty(context.ReadControlFile("root.lock"));
        Assert.Equal(new[] { RootMembershipRegistry.LedgerName, "root.lock" }.OrderBy(name => name, StringComparer.Ordinal),
            context.DirectoryEntryNames(RootMembershipRegistry.ControlDirectoryName));
        Assert.NotNull(context.Files.InspectChildNoFollow(context.Root, RootMembershipRegistry.ControlDirectoryName));
        context.AssertStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_PreconditionRefusalDoesNotMutateTheRootOrMemberStateParents()
    {
        using var context = InitializationContext.Create();
        var originalNames = context.RootEntryNames();
        var declarations = context.DeclaredMembers;

        var unconfirmed = Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.InitializeIncomplete(
            context.Root, context.RootIdentity, EnrollmentEpoch, declarations, false, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, unconfirmed.Reason);
        Assert.Equal(originalNames, context.RootEntryNames());

        var wrongRootIdentity = new PhysicalRootIdentity(new PhysicalFileIdentity(
            context.RootIdentity.HandleIdentity.Provider,
            context.RootIdentity.HandleIdentity.VolumeOrDeviceId,
            context.RootIdentity.HandleIdentity.FileId + "-wrong"));
        var wrongRoot = Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.InitializeIncomplete(
            context.Root, wrongRootIdentity, EnrollmentEpoch, declarations, true, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, wrongRoot.Reason);
        Assert.Equal(originalNames, context.RootEntryNames());

        var empty = Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.InitializeIncomplete(
            context.Root, context.RootIdentity, EnrollmentEpoch, [], true, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, empty.Reason);
        Assert.Equal(originalNames, context.RootEntryNames());

        var duplicate = new[] { declarations[0], declarations[0] };
        Assert.Throws<ArgumentException>(() => context.Registry.InitializeIncomplete(
            context.Root, context.RootIdentity, EnrollmentEpoch, duplicate, true, CancellationToken.None));
        Assert.Equal(originalNames, context.RootEntryNames());

        var nonDeclared = new RootMemberRecord("existing", declarations[0].ConfiguredLocator,
            new RootMemberRecord.ProspectiveBinding(
                context.Files.InspectHandle(context.StateParents[0]).Identity,
                context.StateNameSemantics[0],
                Path.GetFileName(context.StatePaths[0])));
        var mixedBindings = new[] { declarations[0], nonDeclared };
        var nonDeclaredRefusal = Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.InitializeIncomplete(
            context.Root, context.RootIdentity, EnrollmentEpoch, mixedBindings, true, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, nonDeclaredRefusal.Reason);
        Assert.Equal(originalNames, context.RootEntryNames());
        context.AssertStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_PresentFinalWithMissingLedgerIsPreserved()
        => AssertExistingFinalPreserved(malformedLedger: false);

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_PresentFinalWithMalformedLedgerIsPreserved()
        => AssertExistingFinalPreserved(malformedLedger: true);

    private static void AssertExistingFinalPreserved(bool malformedLedger)
    {
        using var context = InitializationContext.Create();
        var controlIdentity = context.CreateExistingControl(malformedLedger ? "not-a-ledger"u8.ToArray() : null);
        var before = context.CaptureControlEntries();

        Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.InitializeIncomplete(
            context.Root, context.RootIdentity, EnrollmentEpoch, context.DeclaredMembers, true, CancellationToken.None));

        Assert.Equal(controlIdentity, context.Files.InspectChildNoFollow(context.Root, RootMembershipRegistry.ControlDirectoryName)!.Identity);
        Assert.Equal(before, context.CaptureControlEntries());
        context.AssertStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_CancellationOrFaultBeforeMoveRetainsStageAndLeavesFinalAbsent()
    {
        using var cancelledContext = InitializationContext.Create();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => cancelledContext.Registry.InitializeIncomplete(
            cancelledContext.Root,
            cancelledContext.RootIdentity,
            EnrollmentEpoch,
            cancelledContext.DeclaredMembers,
            true,
            cancellation.Token,
            point =>
            {
                if (point == RootMembershipEnrollmentPoint.StagingCreated)
                    cancellation.Cancel();
            }));

        Assert.Null(cancelledContext.Files.InspectChildNoFollow(cancelledContext.Root, RootMembershipRegistry.ControlDirectoryName));
        var emptyStage = Assert.Single(cancelledContext.EnrollmentStages());
        Assert.Empty(cancelledContext.DirectoryEntryNames(emptyStage));
        cancelledContext.AssertStateParentsUnchanged();

        using var faultedContext = InitializationContext.Create();
        Assert.Throws<InjectedInitializationFault>(() => faultedContext.Registry.InitializeIncomplete(
            faultedContext.Root,
            faultedContext.RootIdentity,
            EnrollmentEpoch,
            faultedContext.DeclaredMembers,
            true,
            CancellationToken.None,
            point =>
            {
                if (point == RootMembershipEnrollmentPoint.DeclarationPrepared)
                    throw new InjectedInitializationFault();
            }));

        Assert.Null(faultedContext.Files.InspectChildNoFollow(faultedContext.Root, RootMembershipRegistry.ControlDirectoryName));
        var preparedStage = Assert.Single(faultedContext.EnrollmentStages());
        Assert.Contains(RootMembershipRegistry.LedgerName, faultedContext.DirectoryEntryNames(preparedStage));
        Assert.Contains("root.lock", faultedContext.DirectoryEntryNames(preparedStage));
        var stagedLedger = faultedContext.ReadLedger(preparedStage);
        AssertDeclaredIncomplete(faultedContext, stagedLedger);
        faultedContext.AssertStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public void InitializeIncomplete_ExceptionAfterMoveLeavesExactValidIncompleteDeclaration()
    {
        using var context = InitializationContext.Create();
        var orphanIdentity = context.CreateOrphanEnrollmentStage(".nuplane-enrollment-unknown");

        Assert.Throws<InjectedInitializationFault>(() => context.Registry.InitializeIncomplete(
            context.Root,
            context.RootIdentity,
            EnrollmentEpoch,
            context.DeclaredMembers,
            true,
            CancellationToken.None,
            point =>
            {
                if (point == RootMembershipEnrollmentPoint.ControlPublished)
                    throw new InjectedInitializationFault();
            }));

        var final = context.Registry.ReadCandidate(context.Root);
        AssertDeclaredIncomplete(context, final);
        Assert.Empty(context.ReadControlFile("root.lock"));
        Assert.Equal(orphanIdentity, context.Files.InspectChildNoFollow(context.Root, ".nuplane-enrollment-unknown")!.Identity);
        Assert.Equal("orphan-stage-evidence"u8.ToArray(), context.ReadFileFromDirectory(".nuplane-enrollment-unknown", "evidence.bin"));
        Assert.DoesNotContain(context.EnrollmentStages(), name => name != ".nuplane-enrollment-unknown");
        context.AssertStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task InitializeIncomplete_CompetingPublishersCannotOverwriteWinnerAndRetainLosingStage()
    {
        using var context = InitializationContext.Create();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var declarations = new[]
        {
            new[] { context.Declare("first-a"), context.Declare("first-b") },
            new[] { context.Declare("second") }
        };
        var registries = new[] { context.Registry, context.NewRegistry() };

        Task<RootMembershipRecord> Publish(int index) => Task.Run(() => registries[index].InitializeIncomplete(
            context.Root,
            context.RootIdentity,
            EnrollmentEpoch + index,
            declarations[index],
            true,
            CancellationToken.None,
            point =>
            {
                if (point != RootMembershipEnrollmentPoint.DeclarationPrepared)
                    return;
                ready.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The competing declaration publisher did not rendezvous.");
            }));

        var first = Publish(0);
        var second = Publish(1);
        var bothPrepared = ready.Wait(TimeSpan.FromSeconds(15));
        release.Set();
        var outcomes = await Task.WhenAll(Observe(first), Observe(second));
        Assert.True(bothPrepared, "Both declarations must be prepared before either can publish.");
        var winner = Assert.Single(outcomes, outcome => outcome.Record is not null).Record!;
        Assert.Single(outcomes, outcome => outcome.Error is PackageStoreAdmissionException);
        var expectedWinner = declarations.Single(declared => declared.Select(member => member.MemberId)
            .SequenceEqual(winner.Members.Select(member => member.MemberId)));
        AssertDeclaredIncomplete(context, context.Registry.ReadCandidate(context.Root), expectedWinner);

        var losingStage = Assert.Single(context.EnrollmentStages());
        var losingLedger = context.ReadLedger(losingStage);
        Assert.NotEqual(winner.LedgerDigest, losingLedger.LedgerDigest);
        Assert.Equal(RootMembershipStatus.Incomplete, losingLedger.Status);
        context.AssertStateParentsUnchanged();

        static async Task<PublishOutcome> Observe(Task<RootMembershipRecord> task)
        {
            try { return new PublishOutcome(await task.ConfigureAwait(false), null); }
            catch (Exception exception) { return new PublishOutcome(null, exception); }
        }
    }

    private static void AssertDeclaredIncomplete(
        InitializationContext context,
        RootMembershipRecord record,
        IEnumerable<RootMemberRecord>? expectedMembers = null)
    {
        var expected = expectedMembers ?? context.DeclaredMembers;
        Assert.Equal(RootMembershipStatus.Incomplete, record.Status);
        Assert.Equal(context.RootIdentity, record.RootIdentity);
        Assert.Equal(expected.Select(member => (member.MemberId, member.ConfiguredLocator)),
            record.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(record.Members.Select(member => member.MemberId), record.TargetMemberIds);
        Assert.All(record.Members, member => Assert.IsType<RootMemberRecord.DeclaredBinding>(member.Binding));
        Assert.Empty(record.RetiredMembers);
        Assert.Null(record.PendingStateCommit);
    }

    private sealed class InitializationContext : IDisposable
    {
        private readonly Dictionary<string, SortedDictionary<string, string>> _stateParentEvidence = new(StringComparer.Ordinal);
        private int _nextDeclaredIndex;

        private InitializationContext()
        {
            Files = OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
            Fixture = new PackageStoreFixture();
        }

        internal IPhysicalStoreFileSystem Files { get; }
        internal PackageStoreFixture Fixture { get; }
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal PhysicalStoreDirectoryHandle[] StateParents { get; private set; } = [];
        internal string[] StatePaths { get; private set; } = [];
        internal PhysicalStoreNameSemantics[] StateNameSemantics { get; private set; } = [];
        internal RootMembershipRegistry Registry { get; private set; } = null!;
        internal RootMemberRecord[] DeclaredMembers { get; private set; } = [];
        private string[] StateParentPaths { get; set; } = [];

        internal static InitializationContext Create(int memberCount = 2)
        {
            var context = new InitializationContext();
            try
            {
                context.Initialize(memberCount);
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        internal RootMemberRecord Declare(string memberId)
        {
            var index = _nextDeclaredIndex++ % StatePaths.Length;
            return new RootMemberRecord(memberId, StatePaths[index], new RootMemberRecord.DeclaredBinding());
        }

        internal RootMembershipRegistry NewRegistry()
            => new(Files, new StoreStateSerializer());

        internal PhysicalFileIdentity CreateExistingControl(byte[]? ledger)
        {
            using var control = Files.CreateDirectoryExclusiveAt(Root, RootMembershipRegistry.ControlDirectoryName);
            CreateFile(control, "root.lock", []);
            CreateFile(control, "sentinel.bin", "do-not-repair-existing-control"u8.ToArray());
            if (ledger is not null)
                CreateFile(control, RootMembershipRegistry.LedgerName, ledger);
            return Files.InspectHandle(control).Identity;
        }

        internal PhysicalFileIdentity CreateOrphanEnrollmentStage(string name)
        {
            using var stage = Files.CreateDirectoryExclusiveAt(Root, name);
            CreateFile(stage, "evidence.bin", "orphan-stage-evidence"u8.ToArray());
            return Files.InspectHandle(stage).Identity;
        }

        internal string[] RootEntryNames()
            => Directory.EnumerateFileSystemEntries(Fixture.PackageInstallRoot)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()!;

        internal string[] EnrollmentStages()
            => RootEntryNames()
                .Where(name => name is not null && name.StartsWith(RootMembershipRegistry.EnrollmentStagePrefix, StringComparison.Ordinal))
                .Select(name => name!)
                .ToArray();

        internal string[] DirectoryEntryNames(string name)
        {
            using var directory = Files.OpenDirectoryChildNoFollow(Root, name);
            return Directory.EnumerateFileSystemEntries(Path.Combine(Fixture.PackageInstallRoot, name))
                .Select(Path.GetFileName)
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray()!;
        }

        internal RootMembershipRecord ReadLedger(string directoryName)
        {
            using var directory = Files.OpenDirectoryChildNoFollow(Root, directoryName);
            using var file = Files.OpenFileChildNoFollow(directory, RootMembershipRegistry.LedgerName, FileAccess.Read);
            var bytes = Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes);
            return new RootMembershipPayloadSerializer().Deserialize(bytes);
        }

        internal byte[] ReadControlFile(string name)
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            using var file = Files.OpenFileChildNoFollow(control, name, FileAccess.Read);
            return Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes);
        }

        internal string[] CaptureControlEntries()
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            return Directory.EnumerateFileSystemEntries(Path.Combine(Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName))
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => $"{name}:{EntryDigest(control, name!)}")
                .ToArray()!;
        }

        internal byte[] ReadFileFromDirectory(string directoryName, string fileName)
        {
            using var directory = Files.OpenDirectoryChildNoFollow(Root, directoryName);
            using var file = Files.OpenFileChildNoFollow(directory, fileName, FileAccess.Read);
            return Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes);
        }

        internal void AssertStateParentsUnchanged()
        {
            for (var i = 0; i < StateParents.Length; i++)
            {
                var parent = StateParents[i];
                Assert.Equal(_stateParentEvidence[StatePaths[i]], CaptureStateParentEvidence(parent, StateParentPaths[i]));
            }
        }

        public void Dispose()
        {
            foreach (var parent in StateParents.Reverse())
                parent?.Dispose();
            Root?.Dispose();
            Fixture.Dispose();
        }

        private void Initialize(int memberCount)
        {
            if (memberCount < 1 || memberCount > 8)
                throw new ArgumentOutOfRangeException(nameof(memberCount));

            Root = PhysicalStoreTestDirectory.Open(Files, Fixture.PackageInstallRoot);
            StatePaths = new string[memberCount];
            StateParentPaths = new string[memberCount];
            StateParents = new PhysicalStoreDirectoryHandle[memberCount];
            StateNameSemantics = new PhysicalStoreNameSemantics[memberCount];
            for (var i = 0; i < memberCount; i++)
            {
                var relative = i == 0 ? "state/store-state.json" : $"state-{i}/store-state.json";
                StatePaths[i] = i == 0 ? Fixture.StateFilePath : Fixture.CreateStateSlot(relative);
                StateParentPaths[i] = Path.GetDirectoryName(StatePaths[i])!;
                StateParents[i] = PhysicalStoreTestDirectory.Open(Files, StateParentPaths[i]);
                var stateBytes = System.Text.Encoding.UTF8.GetBytes($"member-state-{i}-must-remain-unread-and-unchanged");
                CreateFile(StateParents[i], Path.GetFileName(StatePaths[i]), stateBytes);

                var marker = CreateFile(StateParents[i], $"profile-{i}.marker", []);
                StateNameSemantics[i] = ((IPhysicalStoreNameFileSystem)Files)
                    .ObserveCanonicalFileNameNoFollow(StateParents[i], $"profile-{i}.marker", marker).Semantics;

                _stateParentEvidence.Add(StatePaths[i], CaptureStateParentEvidence(StateParents[i], StateParentPaths[i]));
            }

            RootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
            DeclaredMembers = Enumerable.Range(0, memberCount)
                .Select(index => Declare($"member-{index + 1}"))
                .ToArray();
            Registry = new RootMembershipRegistry(Files, new StoreStateSerializer());
            AssertStateParentsUnchanged();
        }

        private PhysicalFileIdentity CreateFile(PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
        {
            using var file = Files.CreateFileExclusiveAt(parent, name);
            Files.WriteNewControlFile(file, bytes);
            return Files.InspectHandle(file).Identity;
        }

        private SortedDictionary<string, string> CaptureStateParentEvidence(
            PhysicalStoreDirectoryHandle parent,
            string parentPath)
        {
            var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["@parent"] = Files.InspectHandle(parent).Identity.ToString()
            };
            foreach (var path in Directory.EnumerateFileSystemEntries(parentPath))
            {
                var name = Path.GetFileName(path);
                var entry = Files.InspectChildNoFollow(parent, name)
                    ?? throw new InvalidDataException("An external state-parent entry disappeared during snapshotting.");
                var content = string.Empty;
                if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                {
                    using var file = Files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
                    content = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
                }
                evidence.Add(name, $"{entry.Kind}|{entry.Identity}|{entry.LinkCount}|{entry.Length}|{content}");
            }

            return evidence;
        }

        private string EntryDigest(PhysicalStoreDirectoryHandle parent, string name)
        {
            var entry = Files.InspectChildNoFollow(parent, name)
                ?? throw new InvalidDataException("A control entry disappeared while capturing the snapshot.");
            if (entry.Kind != PhysicalStoreEntryKind.RegularFile)
                return $"{entry.Kind}:{entry.Identity}";
            using var file = Files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
            return $"{entry.Kind}:{entry.Identity}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)))}";
        }

    }

    private sealed class InjectedInitializationFault : Exception { }
    private sealed record PublishOutcome(RootMembershipRecord? Record, Exception? Error);
}
