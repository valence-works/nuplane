using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreControlRecoveryTests
{
    [SupportedPhysicalStoreFact]
    public async Task RemoveLockedControlFile_ConsumesExactLockedHandleAndVerifiesAbsence()
    {
        using var context = CreateContext();
        var expected = CreateFile(context, "artifact.control", "journal"u8.ToArray());

        var locked = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "artifact.control", expected);
        Assert.NotNull(locked);
        await using (locked)
        {
            await context.Recovery.RemoveLockedControlFileAsync(locked);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "artifact.control"));

            var consumed = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => context.Recovery.RemoveLockedControlFileAsync(locked).AsTask());
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, consumed.Reason);
            var inspectConsumed = Assert.Throws<PackageStoreAdmissionException>(
                () => context.Recovery.InspectLockedControlFile(locked));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, inspectConsumed.Reason);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectLockedControlFile_ReplaysEmptySentinelMetadataWithoutReleasingOwnership()
    {
        using var context = CreateContext();
        var expected = CreateFile(context, "inspect-sentinel.control", []);
        var token = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(
            context.Parent, "inspect-sentinel.control", expected);
        Assert.NotNull(token);
        await using (token)
        {
            var first = context.Recovery.InspectLockedControlFile(token);
            var second = context.Recovery.InspectLockedControlFile(token);
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, first.Kind);
            Assert.Equal(expected, first.Identity);
            Assert.Equal(1UL, first.LinkCount);
            Assert.Equal(0L, first.Length);
            Assert.Equal(first, second);

            var busy = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(
                context.Parent, "inspect-sentinel.control", expected);
            Assert.Null(busy);

            var foreign = CreateRecoveryFileSystem();
            var foreignError = Assert.Throws<PackageStoreAdmissionException>(
                () => foreign.InspectLockedControlFile(token));
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignError.Reason);
        }

        var disposedError = Assert.Throws<PackageStoreAdmissionException>(
            () => context.Recovery.InspectLockedControlFile(token));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, disposedError.Reason);
        Assert.Equal(expected, RequireEntry(context, "inspect-sentinel.control").Identity);
    }

    [SupportedPhysicalStoreFact]
    public async Task TryOpenAndLockControlFile_ReturnsNullOnlyForBusyAndKeepsCurrentOwnerLocked()
    {
        using var context = CreateContext();
        var expected = CreateFile(context, "busy.control", "busy"u8.ToArray());
        var incumbent = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "busy.control", expected);
        Assert.NotNull(incumbent);
        await using (incumbent)
        {
            // These compatible opens must reach the nonblocking native lock. An ordinary
            // Windows handle that does not share DELETE is covered separately as Unknown.
            var firstBusyAttempt = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "busy.control", expected);
            Assert.Null(firstBusyAttempt);
            var secondBusyAttempt = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "busy.control", expected);
            Assert.Null(secondBusyAttempt);
        }

        Assert.Equal(expected, RequireEntry(context, "busy.control").Identity);
        Assert.Equal("busy"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/busy.control")));
        var available = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "busy.control", expected);
        Assert.NotNull(available);
        await using (available)
            await context.Recovery.RemoveLockedControlFileAsync(available);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "busy.control"));
    }

    [SupportedPhysicalStoreFact]
    public async Task DisposeWithoutRemoval_ReleasesLockAndPreservesFile()
    {
        using var context = CreateContext();
        var contents = "keep for retry"u8.ToArray();
        var expected = CreateFile(context, "retry.control", contents);

        var first = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "retry.control", expected);
        Assert.NotNull(first);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(expected, RequireEntry(context, "retry.control").Identity);
        Assert.Equal(contents, File.ReadAllBytes(context.Fixture.GetPath("parent/retry.control")));

        var retry = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "retry.control", expected);
        Assert.NotNull(retry);
        await using (retry)
            await context.Recovery.RemoveLockedControlFileAsync(retry);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "retry.control"));

        var disposed = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => context.Recovery.RemoveLockedControlFileAsync(first).AsTask());
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, disposed.Reason);
    }

    [SupportedPhysicalStoreFact]
    public async Task RemovalTokenRetainsItsHeldParentLeaseUntilPostCloseAbsenceCheck()
    {
        using var context = CreateContext();
        var expected = CreateFile(context, "held-parent.control", "held parent"u8.ToArray());
        var locked = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "held-parent.control", expected);
        Assert.NotNull(locked);
        await using (locked)
        {
            context.Parent.Dispose();
            await context.Recovery.RemoveLockedControlFileAsync(locked);
            Assert.False(File.Exists(context.Fixture.GetPath("parent/held-parent.control")));
        }
    }

    [SupportedPhysicalStoreFact]
    public void RemovalToken_SerializesDisposeThroughPostCloseVerification()
    {
        using var context = CreateContext();
        CreateFile(context, "token-parent.control", "parent"u8.ToArray());
        CreateFile(context, "token-file.control", "file"u8.ToArray());

        var owner = new object();
        var parentFileHandle = File.OpenHandle(
            context.Fixture.GetPath("parent/token-parent.control"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var parentClosed = new ManualResetEventSlim();
        using var parentHandle = new ReleaseObservingSafeFileHandle(parentFileHandle, parentClosed);
        var parent = new PhysicalStoreDirectoryHandle(owner, parentHandle);
        var parentLease = parent.AcquireScopedSafeHandle(owner);
        var parentRaw = parentLease.DangerousHandle;
        parent.Dispose();

        var fileInnerHandle = File.OpenHandle(
            context.Fixture.GetPath("parent/token-file.control"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var fileClosed = new ManualResetEventSlim();
        using var fileHandle = new ReleaseObservingSafeFileHandle(fileInnerHandle, fileClosed);
        var file = new PhysicalStoreFileHandle(owner, fileHandle, createdExclusive: false);
        var fileLease = file.AcquireScopedSafeHandle(owner);
        var identity = new PhysicalFileIdentity("test", "volume", "file");
        var canonicalName = new PhysicalStoreCanonicalName(
            identity,
            identity,
            "token-file.control",
            new PhysicalStoreNameSemantics("test-profile", PhysicalStoreNameEncoding.Utf8, caseSensitive: true, normalizationInsensitive: false));
        var token = new PhysicalStoreLockedControlFile(owner, file, fileLease, parentLease, canonicalName, _ => { });

        using var verificationEntered = new ManualResetEventSlim();
        using var verificationChecksPassed = new ManualResetEventSlim();
        using var allowVerification = new ManualResetEventSlim();
        using var disposeCallStarted = new ManualResetEventSlim();
        using var disposeCompleted = new ManualResetEventSlim();
        using var removalCompleted = new ManualResetEventSlim();
        Exception? removalFailure = null;
        Exception? disposeFailure = null;
        Exception? testFailure = null;
        Exception? cleanupFailure = null;
        var disposerStarted = false;

        var remover = new Thread(() =>
        {
            try
            {
                token.WithRemovalAttempt(
                    owner,
                    (_, _, markMutationAttempted) =>
                    {
                        markMutationAttempted();
                        return true;
                    },
                    _ =>
                    {
                        verificationEntered.Set();
                        Assert.True(fileClosed.IsSet, "The consumed file's native handle must be released before post-close verification.");
                        Assert.True(IsNativeHandleOpen(parentRaw), "The held parent lease must remain open throughout verification.");
                        verificationChecksPassed.Set();
                        if (!allowVerification.Wait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("The test did not release post-close verification.");
                        Assert.True(IsNativeHandleOpen(parentRaw), "Concurrent disposal released the parent before verification completed.");
                        Assert.False(parentClosed.IsSet, "The parent SafeHandle was released before post-close verification completed.");
                    });
            }
            catch (Exception exception)
            {
                removalFailure = exception;
            }
            finally
            {
                removalCompleted.Set();
            }
        }) { IsBackground = true, Name = "control-recovery-removal" };

        var disposer = new Thread(() =>
        {
            disposeCallStarted.Set();
            try
            {
                token.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }
            finally
            {
                disposeCompleted.Set();
            }
        }) { IsBackground = true, Name = "control-recovery-disposal" };

        remover.Start();
        try
        {
            Assert.True(verificationEntered.Wait(TimeSpan.FromSeconds(10)), "Post-close verification did not start.");
            var verifierState = WaitHandle.WaitAny(
                [verificationChecksPassed.WaitHandle, removalCompleted.WaitHandle],
                TimeSpan.FromSeconds(10));
            Assert.True(verifierState != WaitHandle.WaitTimeout, "Post-close verification did not complete its native-release check.");
            Assert.True(verifierState != 1, $"Post-close verification exited before checking native release: {removalFailure?.Message}");
            disposer.Start();
            disposerStarted = true;
            Assert.True(disposeCallStarted.Wait(TimeSpan.FromSeconds(10)), "Concurrent disposal did not start.");

            // Dedicated threads and an event wait let the competing disposal make progress if the
            // verifier ever escapes the token gate. The native parent-handle check is the causal
            // assertion: outside the gate, disposal closes the lease while verification is paused.
            Assert.False(
                disposeCompleted.Wait(TimeSpan.FromMilliseconds(500)),
                "Concurrent disposal completed while post-close verification still owned the token gate.");
            Assert.True(IsNativeHandleOpen(parentRaw));
            Assert.False(parentClosed.IsSet);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            allowVerification.Set();
            try
            {
                if (!remover.Join(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Removal thread did not finish.");
                if (disposerStarted)
                {
                    if (!disposer.Join(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Disposal thread did not finish.");
                }
                else
                {
                    token.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (testFailure is not null && cleanupFailure is not null)
            throw new AggregateException("The token concurrency assertion and cleanup both failed.", testFailure, cleanupFailure);
        if (testFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testFailure).Throw();
        if (cleanupFailure is not null)
            throw cleanupFailure;

        Assert.Null(removalFailure);
        Assert.Null(disposeFailure);
        Assert.True(fileHandle.IsClosed);
        Assert.True(fileClosed.IsSet, "The consumed file's native handle should be released before method completion.");
        Assert.True(parentClosed.IsSet, "The held parent SafeHandle should close after verification and disposal.");
    }

    [SupportedPhysicalStoreFact]
    public async Task LockedToken_IsProviderBoundAndStaleIdentityCannotRemoveReplacement()
    {
        using var context = CreateContext();
        var original = CreateFile(context, "replacement.control", "before"u8.ToArray());
        var locked = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "replacement.control", original);
        Assert.NotNull(locked);
        await using (locked)
        {
            var foreign = CreateRecoveryFileSystem();
            var foreignError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => foreign.RemoveLockedControlFileAsync(locked).AsTask());
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, foreignError.Reason);

            await context.Recovery.RemoveLockedControlFileAsync(locked);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "replacement.control"));
        }

        var prior = CreateFile(context, "stale.control", "old"u8.ToArray());
        var replacement = CreateFile(context, "replacement-source.control", "new"u8.ToArray());
        File.Delete(context.Fixture.GetPath("parent/stale.control"));
        File.Move(
            context.Fixture.GetPath("parent/replacement-source.control"),
            context.Fixture.GetPath("parent/stale.control"));
        Assert.NotEqual(prior, replacement);
        var staleError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "stale.control", prior).AsTask());
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, staleError.Reason);
        Assert.Equal(replacement, RequireEntry(context, "stale.control").Identity);
        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/stale.control")));
    }

    [SupportedUnixFact]
    public async Task RemoveLockedControlFile_RefusesNameReplacedAfterTokenAcquisition()
    {
        using var context = CreateContext();
        var originalBytes = "locked original"u8.ToArray();
        var replacementBytes = "replacement survives"u8.ToArray();
        var originalIdentity = CreateFile(context, "replaced-after-lock.control", originalBytes);
        var locked = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(
            context.Parent,
            "replaced-after-lock.control",
            originalIdentity);
        Assert.NotNull(locked);

        var originalPath = context.Fixture.GetPath("parent/replaced-after-lock.control");
        var displacedPath = context.Fixture.GetPath("parent/displaced-original.control");
        await using (locked)
        {
            File.Move(originalPath, displacedPath);
            File.WriteAllBytes(originalPath, replacementBytes);

            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => context.Recovery.RemoveLockedControlFileAsync(locked).AsTask());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        }

        Assert.NotEqual(originalIdentity, RequireEntry(context, "replaced-after-lock.control").Identity);
        Assert.Equal(replacementBytes, File.ReadAllBytes(originalPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(displacedPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task RemovalRefusesLinksSpecialEntriesAndNonSingleNames()
    {
        using var context = CreateContext();
        var target = CreateFile(context, "target.control", "target"u8.ToArray());
        File.CreateSymbolicLink(context.Fixture.GetPath("parent/link.control"), "target.control");
        var link = RequireEntry(context, "link.control");

        var linkError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "link.control", link.Identity).AsTask());
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, linkError.Reason);
        Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, RequireEntry(context, "link.control").Kind);
        Assert.Equal("target"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/target.control")));

        using (context.Files.CreateDirectoryExclusiveAt(context.Parent, "directory.control"))
        {
            var directory = RequireEntry(context, "directory.control");
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "directory.control", directory.Identity).AsTask());
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "../target.control", target).AsTask());

        var names = (IPhysicalStoreNameFileSystem)context.Files;
        var semantics = names.ObserveDirectoryNameSemantics(context.Parent);
        if (!semantics.CaseSensitive)
        {
            var canonicalIdentity = CreateFile(context, "Canonical.control", "canonical"u8.ToArray());
            var aliasError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "canonical.control", canonicalIdentity).AsTask());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, aliasError.Reason);
            Assert.Equal(canonicalIdentity, RequireEntry(context, "Canonical.control").Identity);
        }

        CreateHardLink(context.Fixture.GetPath("parent/target.control"), context.Fixture.GetPath("parent/hardlink.control"));
        var hardlink = RequireEntry(context, "hardlink.control");
        Assert.Equal(2UL, hardlink.LinkCount);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "hardlink.control", hardlink.Identity).AsTask());
        Assert.Equal("target"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/target.control")));
        Assert.Equal("target"u8.ToArray(), File.ReadAllBytes(context.Fixture.GetPath("parent/hardlink.control")));
    }

    [SupportedPhysicalStoreFact]
    public void MoveControlFileNoReplace_PreservesIdentityAndBytesAndLeavesOccupiedTargetUnchanged()
    {
        using var context = CreateContext();
        var sourceBytes = "stale use record"u8.ToArray();
        var destinationBytes = "existing record"u8.ToArray();
        var source = CreateFile(context, "use.record", sourceBytes);
        var destination = CreateFile(context, "occupied.record", destinationBytes);

        Assert.Throws<PackageStoreAdmissionException>(() => context.Recovery.MoveControlFileNoReplaceAt(
            context.Parent,
            "use.record",
            source,
            "occupied.record"));

        Assert.Equal(source, RequireEntry(context, "use.record").Identity);
        Assert.Equal(destination, RequireEntry(context, "occupied.record").Identity);
        Assert.Equal(sourceBytes, File.ReadAllBytes(context.Fixture.GetPath("parent/use.record")));
        Assert.Equal(destinationBytes, File.ReadAllBytes(context.Fixture.GetPath("parent/occupied.record")));

        var moved = context.Recovery.MoveControlFileNoReplaceAt(context.Parent, "use.record", source, "use.record.quarantine");
        Assert.Equal(source, moved.Identity);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "use.record"));
        Assert.Equal(source, RequireEntry(context, "use.record.quarantine").Identity);
        Assert.Equal(sourceBytes, File.ReadAllBytes(context.Fixture.GetPath("parent/use.record.quarantine")));
    }

    [SupportedWindowsFact]
    public async Task TryOpenAndLockControlFile_RefusesWindowsHandleThatDoesNotShareDeleteThenRecoversAfterClose()
    {
        using var context = CreateContext();
        var expected = CreateFile(context, "share-delete.control", "delete share"u8.ToArray());
        using (var blocker = new FileStream(
                   context.Fixture.GetPath("parent/share-delete.control"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "share-delete.control", expected).AsTask());
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        }

        var locked = await context.Recovery.TryOpenAndLockControlFileForRemovalAt(context.Parent, "share-delete.control", expected);
        Assert.NotNull(locked);
        await using (locked)
            await context.Recovery.RemoveLockedControlFileAsync(locked);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, "share-delete.control"));
    }

    private static TestContext CreateContext()
    {
        var fixture = new PackageStoreFixture();
        PhysicalStoreDirectoryHandle? parent = null;
        try
        {
            var files = CreateFileSystem();
            fixture.CreateDirectory("parent");
            parent = PhysicalStoreTestDirectory.Open(files, fixture.GetPath("parent"));
            return new TestContext(fixture, files, (IPhysicalStoreControlRecoveryFileSystem)files, parent);
        }
        catch
        {
            parent?.Dispose();
            fixture.Dispose();
            throw;
        }
    }

    private static IPhysicalStoreFileSystem CreateFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static IPhysicalStoreControlRecoveryFileSystem CreateRecoveryFileSystem()
        => (IPhysicalStoreControlRecoveryFileSystem)CreateFileSystem();

    private static PhysicalFileIdentity CreateFile(TestContext context, string name, byte[] contents)
    {
        using var file = context.Files.CreateFileExclusiveAt(context.Parent, name);
        context.Files.WriteNewControlFile(file, contents);
        return context.Files.InspectHandle(file).Identity;
    }

    private static PhysicalStoreEntryInfo RequireEntry(TestContext context, string name)
        => context.Files.InspectChildNoFollow(context.Parent, name)
           ?? throw new Xunit.Sdk.XunitException($"Expected owned test entry '{name}' to exist.");

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? (CreateHardLinkW(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError())
            : CreateHardLinkUnix(existingPath, newPath) == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (result != 0)
            throw new IOException($"The owned hard-link test entry could not be created (native error {result}).");
    }

    private static bool IsNativeHandleOpen(IntPtr handle)
    {
        if (OperatingSystem.IsWindows())
            return GetFileType(handle) != 0;

        var descriptor = handle.ToInt32();
        return OperatingSystem.IsMacOS()
            ? FcntlDarwin(descriptor, getFileDescriptorFlags: 1) >= 0
            : FcntlLinux(descriptor, getFileDescriptorFlags: 1) >= 0;
    }

    private sealed class ReleaseObservingSafeFileHandle : SafeHandle
    {
        private readonly SafeFileHandle _inner;
        private readonly ManualResetEventSlim _released;

        internal ReleaseObservingSafeFileHandle(SafeFileHandle inner, ManualResetEventSlim released)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            _inner = inner;
            _released = released;
            SetHandle(inner.DangerousGetHandle());
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            _inner.Dispose();
            _released.Set();
            return true;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint GetFileType(IntPtr fileHandle);

    [DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlDarwin(int fileDescriptor, int getFileDescriptorFlags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlLinux(int fileDescriptor, int getFileDescriptorFlags);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkDarwin(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkLinux(string existingPath, string newPath);

    private static int CreateHardLinkUnix(string existingPath, string newPath)
        => OperatingSystem.IsMacOS()
            ? CreateHardLinkDarwin(existingPath, newPath)
            : CreateHardLinkLinux(existingPath, newPath);

    private sealed class TestContext(
        PackageStoreFixture fixture,
        IPhysicalStoreFileSystem files,
        IPhysicalStoreControlRecoveryFileSystem recovery,
        PhysicalStoreDirectoryHandle parent) : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = fixture;
        internal IPhysicalStoreFileSystem Files { get; } = files;
        internal IPhysicalStoreControlRecoveryFileSystem Recovery { get; } = recovery;
        internal PhysicalStoreDirectoryHandle Parent { get; } = parent;

        public void Dispose()
        {
            try
            {
                Parent.Dispose();
            }
            finally
            {
                Fixture.Dispose();
            }
        }
    }
}
