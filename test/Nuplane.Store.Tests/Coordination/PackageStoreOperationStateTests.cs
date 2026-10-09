using System.Collections.Concurrent;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageStoreOperationStateTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DisposeAsync_WithOutstandingBorrow_WaitsAndKeepsThatBorrowUsableWhileClosing()
    {
        var held = new GatedRootOwnership();
        var validator = new RecordingPathValidator();
        var state = CreateState(held, validator);
        var borrow = state.Owner.Borrow();

        var close = state.Owner.DisposeAsync().AsTask();
        try
        {
            Assert.False(held.DisposeStarted.Task.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => state.Owner.Borrow());

            borrow.ValidateForInstallPath("packages/module/1.0.0");
            Assert.Equal(new[] { "packages/module/1.0.0" }, validator.Paths);

            borrow.Dispose();
            await held.DisposeStarted.Task.WaitAsync(WaitLimit);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            borrow.Dispose();
            held.Release();
            await close.WaitAsync(WaitLimit);
        }

        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task DisposeAsync_DoubleBorrowDisposal_DoesNotReleaseBeforeAllBorrowsEnd()
    {
        var held = new GatedRootOwnership();
        var state = CreateState(held, new RecordingPathValidator());
        var first = state.Owner.Borrow();
        var second = state.Owner.Borrow();
        var close = state.Owner.DisposeAsync().AsTask();

        try
        {
            first.Dispose();
            first.Dispose();
            Assert.False(held.DisposeStarted.Task.IsCompleted);

            second.Dispose();
            await held.DisposeStarted.Task.WaitAsync(WaitLimit);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            held.Release();
            await close.WaitAsync(WaitLimit);
        }

        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCloseCalls_JoinOneReleaseAndShareItsFailure()
    {
        var releaseFailure = new IOException("held root release failed");
        var held = new GatedRootOwnership(releaseFailure);
        var state = CreateState(held, new RecordingPathValidator());
        var borrow = state.Owner.Borrow();

        var firstClose = state.DisposeOwnerAsync(state.Owner).AsTask();
        var secondClose = state.DisposeOwnerAsync(state.Owner).AsTask();
        try
        {
            Assert.Same(firstClose, secondClose);
            borrow.Dispose();
            await held.DisposeStarted.Task.WaitAsync(WaitLimit);
            Assert.False(firstClose.IsCompleted);
        }
        finally
        {
            borrow.Dispose();
            held.Release();
            var firstError = await Assert.ThrowsAsync<IOException>(() => firstClose.WaitAsync(WaitLimit));
            var secondError = await Assert.ThrowsAsync<IOException>(() => secondClose.WaitAsync(WaitLimit));
            Assert.Same(releaseFailure, firstError);
            Assert.Same(firstError, secondError);
        }

        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task BorrowAndCloseRace_AdmitsOnlyWorkCountedBeforeClose()
    {
        var held = new GatedRootOwnership();
        var state = CreateState(held, new RecordingPathValidator());
        var start = NewSignal();

        var borrowAttempt = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return state.Owner.Borrow();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        });
        Task<Task>? closeAttempt = Task.Run(async () =>
        {
            await start.Task;
            return state.Owner.DisposeAsync().AsTask();
        });

        PackageStoreOperationBorrow? borrow = null;
        Task? close = null;
        try
        {
            start.TrySetResult(true);
            borrow = await borrowAttempt.WaitAsync(WaitLimit);
            close = await closeAttempt.WaitAsync(WaitLimit);
            if (borrow is not null)
            {
                Assert.False(held.DisposeStarted.Task.IsCompleted);
                borrow.Dispose();
            }

            await held.DisposeStarted.Task.WaitAsync(WaitLimit);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            start.TrySetResult(true);
            borrow ??= await borrowAttempt.WaitAsync(WaitLimit);
            borrow?.Dispose();
            held.Release();
            close ??= await closeAttempt.WaitAsync(WaitLimit);
            await close.WaitAsync(WaitLimit);
        }

        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task ValidateForInstallPath_InFlightValidationDelaysRootRelease()
    {
        var held = new GatedRootOwnership();
        var validator = new GatedPathValidator();
        var state = CreateState(held, validator);
        var borrow = state.Owner.Borrow();
        var validation = Task.Run(() => borrow.ValidateForInstallPath("packages/module/2.0.0"));

        Task? close = null;
        try
        {
            await validator.Entered.Task.WaitAsync(WaitLimit);
            close = state.Owner.DisposeAsync().AsTask();
            borrow.Dispose();

            Assert.False(held.DisposeStarted.Task.IsCompleted);
            validator.Release();
            await validation.WaitAsync(WaitLimit);
            await held.DisposeStarted.Task.WaitAsync(WaitLimit);

            held.Release();
            await close.WaitAsync(WaitLimit);
            Assert.Equal(1, held.DisposeCalls);
        }
        finally
        {
            validator.Release();
            borrow.Dispose();
            held.Release();
            close ??= state.Owner.DisposeAsync().AsTask();
            await validation.WaitAsync(WaitLimit);
            await close.WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task ForeignAndExpiredScopes_AreRejectedWithTypedReasons()
    {
        var root = Root("one");
        var otherRoot = Root("two");
        var stateOwnership = new GatedRootOwnership();
        var sameRootOwnership = new GatedRootOwnership();
        var otherRootOwnership = new GatedRootOwnership();
        var state = CreateState(stateOwnership, new RecordingPathValidator(), root);
        var sameRootState = CreateState(sameRootOwnership, new RecordingPathValidator(), root);
        var otherRootState = CreateState(otherRootOwnership, new RecordingPathValidator(), otherRoot);
        var sameRootBorrow = sameRootState.Owner.Borrow();
        var otherRootBorrow = otherRootState.Owner.Borrow();
        var localBorrow = state.Owner.Borrow();
        try
        {
            var sameRootOwnerError = Assert.Throws<PackageStoreAdmissionException>(() => state.Borrow(sameRootState.Owner));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, sameRootOwnerError.Reason);

            var ownerError = Assert.Throws<PackageStoreAdmissionException>(() => state.Borrow(otherRootState.Owner));
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, ownerError.Reason);

            var sameRootBorrowError = Assert.Throws<PackageStoreAdmissionException>(
                () => state.ValidateForInstallPath(sameRootBorrow, "packages/module/1.0.0"));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, sameRootBorrowError.Reason);

            var borrowError = Assert.Throws<PackageStoreAdmissionException>(
                () => state.ValidateForInstallPath(otherRootBorrow, "packages/module/1.0.0"));
            Assert.Equal(PackageStoreAdmissionReason.RootMismatch, borrowError.Reason);

            localBorrow.Dispose();
            var directExpiredError = Assert.Throws<PackageStoreAdmissionException>(
                () => state.ValidateForInstallPath(localBorrow, "packages/module/1.0.0"));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, directExpiredError.Reason);
            var expiredError = Assert.Throws<PackageStoreAdmissionException>(
                () => localBorrow.ValidateForInstallPath("packages/module/1.0.0"));
            Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expiredError.Reason);
        }
        finally
        {
            localBorrow.Dispose();
            sameRootBorrow.Dispose();
            otherRootBorrow.Dispose();
            stateOwnership.Release();
            sameRootOwnership.Release();
            otherRootOwnership.Release();
            await state.Owner.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            await sameRootState.Owner.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            await otherRootState.Owner.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        }
    }

    private static PackageStoreOperationState CreateState(
        IAsyncDisposable heldOwnership,
        IPackageStoreOperationPathValidator validator,
        PhysicalRootIdentity? root = null)
        => new(root ?? Root(Guid.NewGuid().ToString("N")), 1, heldOwnership, validator);

    private static PhysicalRootIdentity Root(string value)
        => new(new PhysicalFileIdentity("test", "volume", value));

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class GatedRootOwnership : IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _release = NewSignal();
        private readonly Exception? _failure;
        private int _disposeCalls;

        public GatedRootOwnership(Exception? failure = null)
        {
            _failure = failure;
        }

        public TaskCompletionSource<bool> DisposeStarted { get; } = NewSignal();
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeStarted.TrySetResult(true);
            return new ValueTask(CompleteReleaseAsync());
        }

        public void Release() => _release.TrySetResult(true);

        private async Task CompleteReleaseAsync()
        {
            await _release.Task.ConfigureAwait(false);
            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }

    private sealed class RecordingPathValidator : IPackageStoreOperationPathValidator
    {
        private readonly ConcurrentQueue<string> _paths = new();
        public string[] Paths => _paths.ToArray();
        public void ValidateForInstallPath(string installPath) => _paths.Enqueue(installPath);
    }

    private sealed class GatedPathValidator : IPackageStoreOperationPathValidator
    {
        private readonly TaskCompletionSource<bool> _release = NewSignal();
        public TaskCompletionSource<bool> Entered { get; } = NewSignal();

        public void ValidateForInstallPath(string installPath)
        {
            Entered.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
        }

        public void Release() => _release.TrySetResult(true);
    }
}
