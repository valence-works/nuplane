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

    private static RecordingPhysicalStoreFileSystem CreateContext(
        PackageStoreFixture fixture,
        out PhysicalStoreDirectoryHandle root,
        out PhysicalStoreDirectoryHandle control,
        out StateSlotIdentity firstSlot,
        out StateSlotIdentity lastSlot)
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
                CreateEmptyFile(files, control, PhysicalStoreLock.GetMemberLockName(firstSlot));
                CreateEmptyFile(files, control, PhysicalStoreLock.GetMemberLockName(lastSlot));
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

    private sealed class RecordingPhysicalStoreFileSystem(IPhysicalStoreFileSystem inner)
        : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem
    {
        private readonly object _gate = new();
        private readonly Dictionary<PhysicalStoreFileHandle, string> _fileNames = new();
        private readonly List<string> _events = [];
        private string? _cancelName;
        private CancellationTokenSource? _cancelSource;

        internal IReadOnlyList<string> Events
        {
            get { lock (_gate) return _events.ToArray(); }
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
            lock (_gate) _events.Clear();
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
            lock (_gate) _fileNames[file] = singleName;
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
            lock (_gate) _fileNames[file] = singleName;
            return file;
        }

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
            => inner.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => inner.WriteNewControlFile(file, contents);

        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var nativeOwner = await inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (nativeOwner is null)
                return null;

            string? name;
            CancellationTokenSource? cancellation = null;
            lock (_gate)
            {
                _fileNames.TryGetValue(file, out name);
                if (name is not null)
                {
                    _events.Add($"acquire:{name}");
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
