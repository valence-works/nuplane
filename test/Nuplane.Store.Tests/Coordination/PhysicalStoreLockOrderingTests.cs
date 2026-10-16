using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreLockOrderingTests
{
    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_SortsMemberLocksAndReleasesInReverseOrder()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out var lastSlot);
        using (root)
        using (control)
        {
            var firstMemberLock = PhysicalStoreLock.GetMemberLockName(firstSlot);
            var lastMemberLock = PhysicalStoreLock.GetMemberLockName(lastSlot);
            await using var owner = await new PhysicalStoreLock(files).AcquireAsync(
                control, [lastSlot, firstSlot], CancellationToken.None);

            Assert.Equal(
                new[] { "acquire:root.lock", $"acquire:{firstMemberLock}", $"acquire:{lastMemberLock}" },
                files.Events);

            await owner.DisposeAsync();

            Assert.Equal(
                new[]
                {
                    "acquire:root.lock",
                    $"acquire:{firstMemberLock}",
                    $"acquire:{lastMemberLock}",
                    $"release:{lastMemberLock}",
                    $"release:{firstMemberLock}",
                    "release:root.lock"
                },
                files.Events);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireAsync_CancellationAfterFirstMemberUnwindsAndLeavesEveryLockReusable()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out var lastSlot);
        using (root)
        using (control)
        using (var cancellation = new CancellationTokenSource())
        {
            var firstMemberLock = PhysicalStoreLock.GetMemberLockName(firstSlot);
            var lastMemberLock = PhysicalStoreLock.GetMemberLockName(lastSlot);
            files.CancelAfterNextAcquisitionOf(firstMemberLock, cancellation);

            var canceled = false;
            IAsyncDisposable? unexpectedOwner = null;
            try
            {
                unexpectedOwner = await new PhysicalStoreLock(files).AcquireAsync(
                    control, [lastSlot, firstSlot], cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            finally
            {
                if (unexpectedOwner is not null)
                    await unexpectedOwner.DisposeAsync();
            }

            Assert.True(canceled, "Cancellation after acquiring the first member lock must abort acquisition.");

            Assert.Equal(
                new[] { "acquire:root.lock", $"acquire:{firstMemberLock}", $"release:{firstMemberLock}", "release:root.lock" },
                files.Events);
            Assert.DoesNotContain($"acquire:{lastMemberLock}", files.Events);

            files.ClearEvents();
            await using var retry = await new PhysicalStoreLock(files).AcquireAsync(
                control, [lastSlot, firstSlot], CancellationToken.None);
            await retry.DisposeAsync();

            Assert.Equal(
                new[]
                {
                    "acquire:root.lock",
                    $"acquire:{firstMemberLock}",
                    $"acquire:{lastMemberLock}",
                    $"release:{lastMemberLock}",
                    $"release:{firstMemberLock}",
                    "release:root.lock"
                },
                files.Events);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_PreparesUnderRootCreatesMissingLocksAndAcquiresSorted()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out var lastSlot, createMemberLocks: false);
        using (root)
        using (control)
        {
            var firstMemberLock = PhysicalStoreLock.GetMemberLockName(firstSlot);
            var lastMemberLock = PhysicalStoreLock.GetMemberLockName(lastSlot);
            var owner = await new PhysicalStoreLock(files).AcquireBootstrapAsync(
                control,
                _ => PrepareSlotsUnderRoot(files, lastSlot, firstSlot),
                CancellationToken.None);

            await using (owner)
            {
                Assert.Equal(
                    BuildSuccessfulBootstrapEvents(firstMemberLock, lastMemberLock, created: true, released: false),
                    files.Events);
            }

            Assert.Equal(
                BuildSuccessfulBootstrapEvents(firstMemberLock, lastMemberLock, created: true, released: true),
                files.Events);

            AssertEmptyLockFile(files, control, firstMemberLock);
            AssertEmptyLockFile(files, control, lastMemberLock);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_BusyLaterMemberUnwindsAndRetryReusesProvisionedLockFiles()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out var lastSlot, createMemberLocks: false);
        using (root)
        using (control)
        {
            var firstMemberLock = PhysicalStoreLock.GetMemberLockName(firstSlot);
            var lastMemberLock = PhysicalStoreLock.GetMemberLockName(lastSlot);
            CreateEmptyFile(files, control, lastMemberLock);
            using var busyFile = files.OpenFileChildNoFollow(control, lastMemberLock, FileAccess.ReadWrite);
            var busyLock = await files.TryAcquireExclusiveLock(busyFile);
            Assert.NotNull(busyLock);
            files.ClearEvents();

            try
            {
                await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PhysicalStoreLock(files).AcquireBootstrapAsync(
                    control,
                    _ => PrepareSlotsUnderRoot(files, lastSlot, firstSlot),
                    CancellationToken.None));

                Assert.Equal(
                    new[]
                    {
                        "acquire:root.lock",
                        "prepare:slots",
                        $"create:{firstMemberLock}",
                        $"write:{firstMemberLock}",
                        $"acquire:{firstMemberLock}",
                        $"release:{firstMemberLock}",
                        "release:root.lock"
                    },
                    files.Events);
            }
            finally
            {
                await busyLock!.DisposeAsync();
            }

            AssertEmptyLockFile(files, control, firstMemberLock);
            AssertEmptyLockFile(files, control, lastMemberLock);
            files.ClearEvents();
            await using (var retry = await new PhysicalStoreLock(files).AcquireBootstrapAsync(
                             control,
                             _ => PrepareSlotsUnderRoot(files, lastSlot, firstSlot),
                             CancellationToken.None))
            {
            }

            Assert.Equal(
                BuildSuccessfulBootstrapEvents(firstMemberLock, lastMemberLock, created: false, released: true),
                files.Events);
            AssertEmptyLockFile(files, control, firstMemberLock);
            AssertEmptyLockFile(files, control, lastMemberLock);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_CancellationAfterFirstMemberUnwindsAndRetryReusesCreatedLocks()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out var lastSlot, createMemberLocks: false);
        using (root)
        using (control)
        using (var cancellation = new CancellationTokenSource())
        {
            var firstMemberLock = PhysicalStoreLock.GetMemberLockName(firstSlot);
            var lastMemberLock = PhysicalStoreLock.GetMemberLockName(lastSlot);
            files.CancelAfterNextAcquisitionOf(firstMemberLock, cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhysicalStoreLock(files).AcquireBootstrapAsync(
                control,
                _ => PrepareSlotsUnderRoot(files, lastSlot, firstSlot),
                cancellation.Token));

            Assert.Equal(
                new[]
                {
                    "acquire:root.lock",
                    "prepare:slots",
                    $"create:{firstMemberLock}",
                    $"write:{firstMemberLock}",
                    $"create:{lastMemberLock}",
                    $"write:{lastMemberLock}",
                    $"acquire:{firstMemberLock}",
                    $"release:{firstMemberLock}",
                    "release:root.lock"
                },
                files.Events);
            AssertEmptyLockFile(files, control, firstMemberLock);
            AssertEmptyLockFile(files, control, lastMemberLock);

            files.ClearEvents();
            await using (var retry = await new PhysicalStoreLock(files).AcquireBootstrapAsync(
                             control,
                             _ => PrepareSlotsUnderRoot(files, lastSlot, firstSlot),
                             CancellationToken.None))
            {
            }

            Assert.Equal(
                BuildSuccessfulBootstrapEvents(firstMemberLock, lastMemberLock, created: false, released: true),
                files.Events);
            AssertEmptyLockFile(files, control, firstMemberLock);
            AssertEmptyLockFile(files, control, lastMemberLock);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_CallbackFailureReleasesRootWithoutProvisioningMembers()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out _, out _, createMemberLocks: false);
        using (root)
        using (control)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PhysicalStoreLock(files).AcquireBootstrapAsync(
                control,
                _ => Task.FromException<IReadOnlyList<StateSlotIdentity>>(new InvalidOperationException("binding failed")),
                CancellationToken.None));

            Assert.Equal(new[] { "acquire:root.lock", "release:root.lock" }, files.Events);
            await AssertRootOnlyLockCanBeReusedAsync(files, control);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_CallbackCancellationReleasesRootWithoutProvisioningMembers()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out _, out _, createMemberLocks: false);
        using (root)
        using (control)
        using (var cancellation = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhysicalStoreLock(files).AcquireBootstrapAsync(
                control,
                _ =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<IReadOnlyList<StateSlotIdentity>>(cancellation.Token);
                },
                cancellation.Token));

            Assert.Equal(new[] { "acquire:root.lock", "release:root.lock" }, files.Events);
            await AssertRootOnlyLockCanBeReusedAsync(files, control);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireBootstrapAsync_DuplicatePreparedSlotsFailBeforeMemberProvisioning()
    {
        using var fixture = new PackageStoreFixture();
        var files = CreateContext(fixture, out var root, out var control, out var firstSlot, out _, createMemberLocks: false);
        using (root)
        using (control)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new PhysicalStoreLock(files).AcquireBootstrapAsync(
                control,
                _ => Task.FromResult<IReadOnlyList<StateSlotIdentity>>([firstSlot, firstSlot]),
                CancellationToken.None));

            Assert.Equal(new[] { "acquire:root.lock", "release:root.lock" }, files.Events);
            Assert.Null(files.InspectChildNoFollow(control, PhysicalStoreLock.GetMemberLockName(firstSlot)));
        }
    }

    private static RecordingPhysicalStoreFileSystem CreateContext(
        PackageStoreFixture fixture,
        out PhysicalStoreDirectoryHandle root,
        out PhysicalStoreDirectoryHandle control,
        out StateSlotIdentity firstSlot,
        out StateSlotIdentity lastSlot,
        bool createMemberLocks = true)
    {
        IPhysicalStoreFileSystem native = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var files = new RecordingPhysicalStoreFileSystem(native);
        root = PhysicalStoreTestDirectory.Open(files, fixture.RootPath);
        try
        {
            control = files.CreateDirectoryExclusiveAt(root, "control");
            try
            {
                CreateEmptyFile(files, control, "root.lock");
                using var states = files.CreateDirectoryExclusiveAt(root, "states");
                using (var first = files.CreateFileExclusiveAt(states, "a.json"))
                    files.WriteNewControlFile(first, "a"u8.ToArray());
                using (var last = files.CreateFileExclusiveAt(states, "z.json"))
                    files.WriteNewControlFile(last, "z"u8.ToArray());

                var identity = new PhysicalStoreIdentity(files);
                firstSlot = identity.ObserveStateSlot(states, "a.json").Slot;
                lastSlot = identity.ObserveStateSlot(states, "z.json").Slot;
                if (createMemberLocks)
                {
                    CreateEmptyFile(files, control, PhysicalStoreLock.GetMemberLockName(firstSlot));
                    CreateEmptyFile(files, control, PhysicalStoreLock.GetMemberLockName(lastSlot));
                }
                files.ClearEvents();
                return files;
            }
            catch
            {
                control.Dispose();
                throw;
            }
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static void CreateEmptyFile(IPhysicalStoreFileSystem files, PhysicalStoreDirectoryHandle parent, string name)
    {
        using var file = files.CreateFileExclusiveAt(parent, name);
        files.WriteNewControlFile(file, ReadOnlyMemory<byte>.Empty);
    }

    private static Task<IReadOnlyList<StateSlotIdentity>> PrepareSlotsUnderRoot(
        RecordingPhysicalStoreFileSystem files,
        params StateSlotIdentity[] slots)
    {
        files.RecordEvent("prepare:slots");
        return Task.FromResult<IReadOnlyList<StateSlotIdentity>>(slots);
    }

    private static string[] BuildSuccessfulBootstrapEvents(
        string firstMemberLock,
        string lastMemberLock,
        bool created,
        bool released)
    {
        var events = new List<string> { "acquire:root.lock", "prepare:slots" };
        if (created)
        {
            events.Add($"create:{firstMemberLock}");
            events.Add($"write:{firstMemberLock}");
            events.Add($"create:{lastMemberLock}");
            events.Add($"write:{lastMemberLock}");
        }

        events.Add($"acquire:{firstMemberLock}");
        events.Add($"acquire:{lastMemberLock}");
        if (released)
        {
            events.Add($"release:{lastMemberLock}");
            events.Add($"release:{firstMemberLock}");
            events.Add("release:root.lock");
        }

        return events.ToArray();
    }

    private static void AssertEmptyLockFile(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle control,
        string name)
    {
        using var file = files.OpenFileChildNoFollow(control, name, FileAccess.Read);
        Assert.Empty(files.ReadControlFile(file, 1));
    }

    private static async Task AssertRootOnlyLockCanBeReusedAsync(
        RecordingPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle control)
    {
        files.ClearEvents();
        await using (var owner = await new PhysicalStoreLock(files).AcquireAsync(
                         control,
                         [],
                         CancellationToken.None))
        {
        }

        Assert.Equal(new[] { "acquire:root.lock", "release:root.lock" }, files.Events);
    }

    internal sealed class RecordingPhysicalStoreFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem, IPhysicalStorePublicationFileSystem,
            IPhysicalStoreDirectoryPublicationFileSystem
    {
        private readonly object _gate = new();
        private readonly Dictionary<PhysicalStoreFileHandle, (string Name, PhysicalFileIdentity Parent)> _fileNames = new();
        private readonly List<string> _events = [];
        private readonly List<(PhysicalFileIdentity Parent, string Name)> _lockAcquisitions = [];
        private string? _cancelName;
        private CancellationTokenSource? _cancelSource;

        internal IReadOnlyList<string> Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        internal IReadOnlyList<(PhysicalFileIdentity Parent, string Name)> LockAcquisitions
        {
            get { lock (_gate) return _lockAcquisitions.ToArray(); }
        }

        internal void CancelAfterNextAcquisitionOf(string name, CancellationTokenSource source)
        {
            lock (_gate)
            {
                _cancelName = name;
                _cancelSource = source;
            }
        }

        internal void ClearEvents()
        {
            lock (_gate)
            {
                _events.Clear();
                _lockAcquisitions.Clear();
            }
        }

        internal void RecordEvent(string eventName)
        {
            lock (_gate) _events.Add(eventName);
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            var file = inner.OpenFileChildNoFollow(parent, singleName, access);
            lock (_gate) _fileNames[file] = (singleName, inner.InspectHandle(parent).Identity);
            return file;
        }

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => inner.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => inner.CreateDirectoryExclusiveAt(parent, singleName);

        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var file = inner.CreateFileExclusiveAt(parent, singleName);
            lock (_gate)
            {
                _fileNames[file] = (singleName, inner.InspectHandle(parent).Identity);
                _events.Add($"create:{singleName}");
            }
            return file;
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => inner.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            inner.WriteNewControlFile(file, contents);
            lock (_gate)
            {
                if (_fileNames.TryGetValue(file, out var observed))
                    _events.Add($"write:{observed.Name}");
            }
        }

        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var nativeOwner = await inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (nativeOwner is null)
                return null;

            string? name;
            PhysicalFileIdentity? parentIdentity;
            CancellationTokenSource? cancellation = null;
            lock (_gate)
            {
                if (_fileNames.TryGetValue(file, out var observed))
                {
                    name = observed.Name;
                    parentIdentity = observed.Parent;
                }
                else
                {
                    name = null;
                    parentIdentity = null;
                }
                if (name is not null)
                {
                    _events.Add($"acquire:{name}");
                    _lockAcquisitions.Add((parentIdentity!, name));
                    if (string.Equals(name, _cancelName, StringComparison.Ordinal))
                    {
                        cancellation = _cancelSource;
                        _cancelName = null;
                        _cancelSource = null;
                    }
                }
            }

            if (name is null)
            {
                await nativeOwner.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The test wrapper did not observe the lock-file name before native acquisition.");
            }

            cancellation?.Cancel();
            return new RecordingLockOwner(this, name, nativeOwner);
        }

        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);

        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => ((IPhysicalStoreNameFileSystem)inner).ObserveDirectoryNameSemantics(parent);

        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => ((IPhysicalStoreDirectoryNameFileSystem)inner).ObserveCanonicalDirectoryNameNoFollow(
                parent, singleName, expectedDirectoryIdentity);

        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName)
            => ((IPhysicalStoreDirectoryPublicationFileSystem)inner).PublishDirectoryNoReplaceAt(
                parent, stagedName, expectedStagedIdentity, destinationName);

        public PhysicalStoreEntryInfo PublishControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string stagedName,
            PhysicalFileIdentity expectedStagedIdentity,
            string destinationName,
            PhysicalFileIdentity? expectedDestinationIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).PublishControlFileAt(parent, stagedName,
                expectedStagedIdentity, destinationName, expectedDestinationIdentity);

        public void RemoveControlFileAt(
            PhysicalStoreDirectoryHandle parent,
            string singleName,
            PhysicalFileIdentity expectedIdentity)
            => ((IPhysicalStorePublicationFileSystem)inner).RemoveControlFileAt(parent, singleName, expectedIdentity);

        private void RecordRelease(string name)
        {
            lock (_gate) _events.Add($"release:{name}");
        }

        private sealed class RecordingLockOwner(
            RecordingPhysicalStoreFileSystem recorder,
            string name,
            IAsyncDisposable innerOwner) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                try
                {
                    await innerOwner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    recorder.RecordRelease(name);
                }
            }
        }
    }
}
