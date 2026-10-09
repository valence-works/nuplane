using System.Text;
using System.Reflection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageGraphUseRecordStoreTests
{
    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesMismatchedHeldRootBeforeCreatingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var actual = fixture.RootIdentity.HandleIdentity;
        var wrongRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            actual.Provider, actual.VolumeOrDeviceId, "not-the-held-root"));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(fixture.Files).PublishAsync(
            fixture.Root, wrongRoot, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_InvalidCandidateMetadataIsRejectedBeforeCreatingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 0, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), (PackageGraphUseSnapshotState)99,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());

        var actual = fixture.RootIdentity.HandleIdentity;
        var foreignRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            actual.Provider, actual.VolumeOrDeviceId, "foreign-root"));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(foreignRoot), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_ReopensExactRecordAndRetainsNativeSentinelUntilDisposal()
    {
        using var fixture = NativeFixture.Create();
        var candidate = fixture.CreateCandidate();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        string recordName;
        string sentinelName;
        PhysicalFileIdentity recordIdentity;
        PhysicalFileIdentity sentinelIdentity;

        await using (var owner = await new PackageGraphUseRecordStore(fixture.Files).PublishAsync(
                         fixture.Root, fixture.RootIdentity, 4, candidate, PackageGraphUseSnapshotState.Pending,
                         CancellationToken.None))
        {
            recordName = owner.RecordName;
            sentinelName = owner.SentinelName;
            recordIdentity = owner.RecordIdentity;
            sentinelIdentity = owner.SentinelIdentity;
            Assert.Equal($"use-{owner.Record.UseId:N}.json", owner.RecordName);
            Assert.Equal($"use-{owner.Record.UseId:N}.sentinel", owner.SentinelName);
            Assert.Equal(GraphUseLifetimeKind.OsExclusiveSentinel, owner.Record.LifetimeKind);
            Assert.Equal(4L, owner.Record.EnrollmentEpoch);
            Assert.Equal(fixture.RootIdentity, owner.Record.RootIdentity);
            Assert.Equal(owner.Record.SentinelIdentity, owner.SentinelIdentity);
            Assert.Equal(PhysicalStoreEntryKind.RegularFile,
                fixture.Files.InspectChildNoFollow(fixture.Control, owner.RecordName)!.Kind);
            var sentinelInfo = fixture.Files.InspectChildNoFollow(fixture.Control, owner.SentinelName)!;
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, sentinelInfo.Kind);
            Assert.Equal(1UL, sentinelInfo.LinkCount);
            Assert.Equal(0L, sentinelInfo.Length);
            var names = Assert.IsAssignableFrom<IPhysicalStoreNameFileSystem>(fixture.Files);
            Assert.Equal(owner.SentinelName,
                names.ObserveCanonicalFileNameNoFollow(fixture.Control, owner.SentinelName, sentinelIdentity).Basename);
            Assert.Equal(owner.RecordName,
                names.ObserveCanonicalFileNameNoFollow(fixture.Control, owner.RecordName, recordIdentity).Basename);

            using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.SentinelName, FileAccess.ReadWrite);
            Assert.Null(await fixture.Files.TryAcquireExclusiveLock(probe));

            byte[] bytes;
            using (var recordFile = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.RecordName, FileAccess.Read))
                bytes = fixture.Files.ReadControlFile(recordFile, GraphUsePayloadSerializer.MaximumPayloadBytes);
            var restored = new GraphUsePayloadSerializer().Deserialize(bytes);
            Assert.Equal(owner.Record.PayloadDigest, restored.PayloadDigest);
            Assert.True(owner.Record.GraphSnapshot.HasSamePayloadAs(restored.GraphSnapshot));
            Assert.Equal(bytes.Length, fixture.Files.InspectChildNoFollow(fixture.Control, owner.RecordName)!.Length);
        }

        var record = fixture.Files.InspectChildNoFollow(fixture.Control, recordName);
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, sentinelName);
        Assert.NotNull(record);
        Assert.NotNull(sentinel);
        Assert.Equal(recordIdentity, record.Identity);
        Assert.Equal(sentinelIdentity, sentinel.Identity);
        Assert.Equal(2, fixture.UseArtifactNames().Length);

        using var retry = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var reacquired = await fixture.Files.TryAcquireExclusiveLock(retry);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesWhenNativeSentinelLockIsBusyAndPreservesCreatedFile()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files);
        hooks.AfterExclusiveCreate = (parent, name) =>
        {
            if (!name.EndsWith(".sentinel", StringComparison.Ordinal))
                return;
            hooks.BusySentinelFile = fixture.Files.OpenFileChildNoFollow(parent, name, FileAccess.ReadWrite);
            hooks.BusySentinelLock = fixture.Files.TryAcquireExclusiveLock(hooks.BusySentinelFile)
                .AsTask().GetAwaiter().GetResult();
            Assert.NotNull(hooks.BusySentinelLock);
        };
        var store = new PackageGraphUseRecordStore(hooks);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Committed,
            CancellationToken.None));

        try
        {
            Assert.NotNull(hooks.BusySentinelFile);
            Assert.NotNull(hooks.BusySentinelLock);
            var files = fixture.UseArtifactNames();
            Assert.Single(files);
            Assert.EndsWith(".sentinel", files[0]!, StringComparison.Ordinal);
        }
        finally
        {
            if (hooks.BusySentinelLock is not null)
                await hooks.BusySentinelLock.DisposeAsync();
            hooks.BusySentinelFile?.Dispose();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_NoReplaceRaceLeavesUnpublishedStageAndNeverReturnsOwnership()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            BeforePublish = (parent, _, destination) =>
            {
                using var raced = fixture.Files.CreateFileExclusiveAt(parent, destination);
                fixture.Files.WriteNewControlFile(raced, Encoding.UTF8.GetBytes("external"));
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Equal(3, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.Contains(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
        var sentinelName = names.Single(name => name!.EndsWith(".sentinel", StringComparison.Ordinal))!;
        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var lockAfterRefusal = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterRefusal);
        await lockAfterRefusal!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RejectsStagedByteTamperingAndLeavesArtifactsForInspection()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            BeforeOpen = (_, name) =>
            {
                if (!name.EndsWith(".json.stage", StringComparison.Ordinal))
                    return;
                var path = Path.Combine(fixture.ControlPath, name);
                var bytes = File.ReadAllBytes(path);
                bytes[0] = (byte)'!';
                File.WriteAllBytes(path, bytes);
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.DoesNotContain(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_CancellationAfterStageFlushReleasesSentinelAndPreservesEvidence()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterWrite = (_, name) =>
            {
                if (name.EndsWith(".json.stage", StringComparison.Ordinal))
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            cancellation.Token));

        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.DoesNotContain(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
        var sentinelName = names.Single(name => name!.EndsWith(".sentinel", StringComparison.Ordinal))!;
        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var lockAfterCancellation = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterCancellation);
        await lockAfterCancellation!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_CancellationAfterNativePublicationLeavesVerifiedArtifactsWithoutOwner()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterPublish = (_, _, destination) =>
            {
                if (destination.EndsWith(".json", StringComparison.Ordinal))
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            cancellation.Token));

        await AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(fixture);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_ProviderFailureAfterNativePublicationLeavesEvidenceWithoutOwner()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterPublish = static (_, _, destination) =>
            {
                if (destination.EndsWith(".json", StringComparison.Ordinal))
                    throw new IOException("Injected failure after the native publication completed.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Committed,
            CancellationToken.None));

        await AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(fixture);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesSentinelTamperingDuringExclusiveInitialization()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files);
        hooks.AfterExclusiveCreate = (parent, name) =>
        {
            if (name.EndsWith(".sentinel", StringComparison.Ordinal))
            {
                hooks.SentinelName = name;
                hooks.SentinelIdentity = fixture.Files.InspectChildNoFollow(parent, name)!.Identity;
                using var stream = new FileStream(Path.Combine(fixture.ControlPath, name), FileMode.Open,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.WriteByte(0x5A);
                stream.Flush(flushToDisk: true);
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Single(names);
        Assert.Contains(names, static name => name is not null && name.EndsWith(".sentinel", StringComparison.Ordinal));
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, hooks.SentinelName!);
        Assert.NotNull(sentinel);
        Assert.Equal(hooks.SentinelIdentity, sentinel.Identity);
        Assert.Equal(1L, sentinel.Length);

        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, hooks.SentinelName!, FileAccess.ReadWrite);
        var lockAfterRefusal = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterRefusal);
        await lockAfterRefusal!.DisposeAsync();
    }

    private static async Task AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(NativeFixture fixture)
    {
        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        var recordName = names.Single(name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) &&
            !name.EndsWith(".json.stage", StringComparison.Ordinal))!;
        var sentinelName = names.Single(name => name is not null && name.EndsWith(".sentinel", StringComparison.Ordinal))!;
        byte[] payload;
        using (var recordFile = fixture.Files.OpenFileChildNoFollow(fixture.Control, recordName, FileAccess.Read))
            payload = fixture.Files.ReadControlFile(recordFile, GraphUsePayloadSerializer.MaximumPayloadBytes);
        var record = new GraphUsePayloadSerializer().Deserialize(payload);
        Assert.Equal($"use-{record.UseId:N}.json", recordName);
        Assert.Equal($"use-{record.UseId:N}.sentinel", sentinelName);
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, sentinelName);
        Assert.NotNull(sentinel);
        Assert.Equal(record.SentinelIdentity, sentinel.Identity);
        Assert.Equal(0L, sentinel.Length);

        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var retry = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(retry);
        await retry!.DisposeAsync();
    }

    private static IPhysicalStoreFileSystem CreateNativeFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private sealed class NativeFixture : IDisposable
    {
        private NativeFixture(
            PackageStoreFixture owner,
            IPhysicalStoreFileSystem files,
            PhysicalStoreDirectoryHandle root,
            PhysicalStoreDirectoryHandle control,
            PhysicalStoreDirectoryHandle install,
            PhysicalRootIdentity rootIdentity,
            string controlPath,
            PhysicalFileIdentity installIdentity)
        {
            Owner = owner;
            Files = files;
            Root = root;
            Control = control;
            Install = install;
            RootIdentity = rootIdentity;
            ControlPath = controlPath;
            InstallIdentity = installIdentity;
        }

        private PackageStoreFixture Owner { get; }
        internal IPhysicalStoreFileSystem Files { get; }
        internal PhysicalStoreDirectoryHandle Root { get; }
        internal PhysicalStoreDirectoryHandle Control { get; }
        private PhysicalStoreDirectoryHandle Install { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal string ControlPath { get; }
        private PhysicalFileIdentity InstallIdentity { get; }

        internal static NativeFixture Create()
        {
            var owner = new PackageStoreFixture();
            var files = CreateNativeFileSystem();
            PhysicalStoreDirectoryHandle? root = null;
            PhysicalStoreDirectoryHandle? control = null;
            PhysicalStoreDirectoryHandle? install = null;
            try
            {
                root = PhysicalStoreTestDirectory.Open(files, owner.PackageInstallRoot);
                var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
                var controlPath = owner.CreateDirectory("packages/.nuplane-store");
                control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
                using (var rootLock = files.CreateFileExclusiveAt(control, "root.lock"))
                    files.WriteNewControlFile(rootLock, ReadOnlyMemory<byte>.Empty);
                var installPath = owner.CreateDirectory("packages/sample/1.0.0");
                install = PhysicalStoreTestDirectory.Open(files, installPath);
                var installIdentity = files.InspectHandle(install).Identity;
                var result = new NativeFixture(owner, files, root, control, install, rootIdentity, controlPath, installIdentity);
                root = null;
                control = null;
                install = null;
                return result;
            }
            catch
            {
                install?.Dispose();
                control?.Dispose();
                root?.Dispose();
                owner.Dispose();
                throw;
            }
        }

        internal ProtectedGraphSnapshot CreateCandidate(PhysicalRootIdentity? rootOverride = null)
        {
            var graphRoot = rootOverride ?? RootIdentity;
            var nodeId = Guid.NewGuid();
            var request = new PackageRequest("Sample.Package", "[1.0.0,2.0.0)", null,
                PackageUpdatePolicy.Range, "graph-use-test");
            var install = new PackageInstallIdentity(graphRoot, request.Id, "1.0.0", "sample/1.0.0",
                InstallIdentity, "completion-v1");
            var node = Internal<PackageGraphNodeIdentity>(nodeId, install);
            var selection = Internal<PackageGraphRootSelection>(request, nodeId);
            var candidate = new ProtectedGraphSnapshot(Guid.NewGuid(), "graph-use-test", Guid.NewGuid().ToString("N"),
                ProtectedGraphDisposition.Active, [graphRoot], [selection], [node], [], recoverySelectionEvidence: null);
            return candidate;
        }

        internal async Task<IAsyncDisposable> HoldRootLockAsync()
        {
            var file = Files.OpenFileChildNoFollow(Control, "root.lock", FileAccess.ReadWrite);
            try
            {
                var nativeLock = await Files.TryAcquireExclusiveLock(file).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The fixture could not acquire its caller-owned root lock.");
                return new NativeLockOwnership(file, nativeLock);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal string?[] UseArtifactNames()
            => Directory.GetFiles(ControlPath).Select(Path.GetFileName)
                .Where(static name => name is not null && name.StartsWith("use-", StringComparison.Ordinal))
                .ToArray();

        public void Dispose()
        {
            Install.Dispose();
            Control.Dispose();
            Root.Dispose();
            Owner.Dispose();
        }

        private static T Internal<T>(params object?[] arguments)
            => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;

        private sealed class NativeLockOwnership(PhysicalStoreFileHandle file, IAsyncDisposable nativeLock) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                try { await nativeLock.DisposeAsync().ConfigureAwait(false); }
                finally { file.Dispose(); }
            }
        }
    }

    internal sealed class PublicationHooks : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem, IPhysicalStoreDirectoryPublicationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStorePublicationFileSystem _publication;
        private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryPublication;
        private readonly Dictionary<PhysicalFileIdentity, string> _createdNames = [];

        internal PublicationHooks(IPhysicalStoreFileSystem files)
        {
            _files = files;
            _names = files as IPhysicalStoreNameFileSystem ?? throw new InvalidOperationException();
            _publication = files as IPhysicalStorePublicationFileSystem ?? throw new InvalidOperationException();
            _directoryPublication = files as IPhysicalStoreDirectoryPublicationFileSystem ?? throw new InvalidOperationException();
        }

        internal Action<PhysicalStoreDirectoryHandle, string>? AfterExclusiveCreate { get; set; }
        internal Action<PhysicalStoreFileHandle, string>? AfterWrite { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string>? BeforeOpen { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? BeforePublish { get; set; }
        internal PhysicalStoreFileHandle? BusySentinelFile { get; set; }
        internal IAsyncDisposable? BusySentinelLock { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? AfterPublish { get; set; }
        internal string? SentinelName { get; set; }
        internal PhysicalFileIdentity? SentinelIdentity { get; set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _files.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => _files.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            BeforeOpen?.Invoke(parent, singleName);
            return _files.OpenFileChildNoFollow(parent, singleName, access);
        }
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => _files.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => _files.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var file = _files.CreateFileExclusiveAt(parent, singleName);
            _createdNames[_files.InspectHandle(file).Identity] = singleName;
            AfterExclusiveCreate?.Invoke(parent, singleName);
            return file;
        }
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes) => _files.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            _files.WriteNewControlFile(file, contents);
            var identity = _files.InspectHandle(file).Identity;
            if (_createdNames.TryGetValue(identity, out var name))
                AfterWrite?.Invoke(file, name);
        }
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
            => _files.TryAcquireExclusiveLock(file);
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
        {
            BeforePublish?.Invoke(parent, stagedName, destinationName);
            var published = _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
            AfterPublish?.Invoke(parent, stagedName, destinationName);
            return published;
        }
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent,
            string singleName, PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryPublication.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => _directoryPublication.PublishDirectoryNoReplaceAt(parent, stagedName, expectedStagedIdentity, destinationName);
    }
}
