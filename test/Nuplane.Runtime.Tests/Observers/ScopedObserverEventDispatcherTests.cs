using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Store.Coordination;

namespace Nuplane.Runtime.Tests.Observers;

public sealed class ScopedObserverEventDispatcherTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly PackageChangeSet Changes = new([], [], [], "scoped-dispatch", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DispatchScopedAsync_EachEventUsesExactOwnerAndExpiresCallbackBorrow(int eventKind)
    {
        var held = new HeldOwnership();
        await using var owner = CreateOwner(held);
        var observer = new ScopedObserver();
        var dispatcher = new ObserverEventDispatcher([observer]);

        await DispatchAsync(dispatcher, owner, eventKind);

        Assert.Equal(1, observer.ScopedCalls);
        Assert.Equal(0, observer.LegacyCalls);
        Assert.Same(owner.Root, observer.LastBorrow!.Root);
        Assert.Equal(owner.Epoch, observer.LastBorrow.Epoch);
        Assert.False(held.Disposed);
        AssertExpired(observer.LastBorrow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DispatchScopedAsync_UnclassifiedLastObserverRefusesBeforeAnyCallback(int eventKind)
    {
        await using var owner = CreateOwner(new HeldOwnership());
        var first = new ScopedObserver();
        var unclassified = new RecordingObserver();
        var dispatcher = new ObserverEventDispatcher([first, unclassified]);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            DispatchAsync(dispatcher, owner, eventKind));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, first.ScopedCalls);
        Assert.Equal(0, first.LegacyCalls);
        Assert.Equal(0, unclassified.LegacyCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DispatchScopedAsync_ExplicitPathIndependentObserverReceivesEvent(int eventKind)
    {
        await using var owner = CreateOwner(new HeldOwnership());
        var observer = new IndependentObserver();

        await DispatchAsync(new ObserverEventDispatcher([observer]), owner, eventKind);

        Assert.Equal(1, observer.LegacyCalls);
    }

    [Fact]
    public async Task DispatchScopedAsync_OwnerCloseWaitsForAwaitedCallbackBorrow()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new HeldOwnership();
        await using var owner = CreateOwner(held);
        var observer = new ScopedObserver(async borrow =>
        {
            entered.TrySetResult(true);
            await release.Task;
            borrow.ValidateForInstallPath("packages/example/1.0.0");
        });
        var dispatch = DispatchAsync(new ObserverEventDispatcher([observer]), owner, 3);
        Task? close = null;
        try
        {
            await entered.Task.WaitAsync(WaitLimit);
            close = owner.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted);
            Assert.False(held.Disposed);
            Assert.False(dispatch.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => owner.Borrow());
        }
        finally
        {
            release.TrySetResult(true);
            await dispatch.WaitAsync(WaitLimit);
            if (close is not null)
                await close.WaitAsync(WaitLimit);
        }
        Assert.True(held.Disposed);
        AssertExpired(observer.LastBorrow!);
    }

    [Fact]
    public async Task DispatchScopedAsync_OrdinaryCallbackFailureDrainsBorrowAndContinues()
    {
        await using var owner = CreateOwner(new HeldOwnership());
        var failed = new ScopedObserver(_ => throw new IOException("observer failed"));
        var next = new ScopedObserver();

        await DispatchAsync(new ObserverEventDispatcher([failed, next]), owner, 1);

        AssertExpired(failed.LastBorrow!);
        Assert.Equal(1, next.ScopedCalls);
        AssertExpired(next.LastBorrow!);
    }

    [Fact]
    public async Task DispatchScopedAsync_AdmissionFailurePropagatesAndDrainsBorrow()
    {
        await using var owner = CreateOwner(new HeldOwnership());
        var refusal = new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch, "wrong root");
        var failed = new ScopedObserver(_ => throw refusal);
        var next = new ScopedObserver();

        var actual = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            DispatchAsync(new ObserverEventDispatcher([failed, next]), owner, 0));

        Assert.Same(refusal, actual);
        AssertExpired(failed.LastBorrow!);
        Assert.Equal(0, next.ScopedCalls);
    }

    [Fact]
    public async Task DispatchScopedAsync_CallbackCancellationPropagatesAndDrainsBorrow()
    {
        await using var owner = CreateOwner(new HeldOwnership());
        using var cancellation = new CancellationTokenSource();
        var failed = new ScopedObserver(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        });
        var next = new ScopedObserver();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DispatchAsync(new ObserverEventDispatcher([failed, next]), owner, 2, cancellation.Token));

        AssertExpired(failed.LastBorrow!);
        Assert.Equal(0, next.ScopedCalls);
    }

    [Fact]
    public async Task DispatchScopedAsync_FinalCallbackIgnoringCancellationStillCancelsAndDrainsBorrow()
    {
        await using var owner = CreateOwner(new HeldOwnership());
        using var cancellation = new CancellationTokenSource();
        var observer = new ScopedObserver(_ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DispatchAsync(new ObserverEventDispatcher([observer]), owner, 3, cancellation.Token));

        AssertExpired(observer.LastBorrow!);
    }

    private static void AssertExpired(PackageStoreOperationBorrow borrow)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(() =>
            borrow.ValidateForInstallPath("packages/example/1.0.0"));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, error.Reason);
    }

    private static Task DispatchAsync(ObserverEventDispatcher dispatcher, PackageStoreOperationOwner owner,
        int eventKind, CancellationToken cancellationToken = default) => eventKind switch
    {
        0 => dispatcher.PublishChangingAsync(Changes, owner, cancellationToken),
        1 => dispatcher.PublishChangedAsync(Changes, owner, cancellationToken),
        2 => dispatcher.NotifyPackageFailedAsync("Example", new IOException("apply failed"),
            Changes.CorrelationId, owner, cancellationToken),
        3 => dispatcher.PublishReconciledAsync(Changes, [], owner, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(eventKind))
    };

    private static PackageStoreOperationOwner CreateOwner(HeldOwnership held)
        => new PackageStoreOperationState(
            new PhysicalRootIdentity(new PhysicalFileIdentity("test", "volume", Guid.NewGuid().ToString("N"))),
            1, held, new PathValidator()).Owner;

    private sealed class HeldOwnership : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PathValidator : IPackageStoreOperationPathValidator
    {
        public void ValidateForInstallPath(string installPath) { }
    }

    private class RecordingObserver : INuplaneObserver
    {
        internal int LegacyCalls { get; private set; }
        private Task RecordAsync()
        {
            LegacyCalls++;
            return Task.CompletedTask;
        }
        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => RecordAsync();
        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => RecordAsync();
        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct) => RecordAsync();
        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> packages,
            CancellationToken ct) => RecordAsync();
    }

    private sealed class IndependentObserver : RecordingObserver, IPackagePathIndependentNuplaneObserver;

    private sealed class ScopedObserver(Func<PackageStoreOperationBorrow, Task>? callback = null)
        : RecordingObserver, IScopedNuplaneObserver
    {
        internal int ScopedCalls { get; private set; }
        internal PackageStoreOperationBorrow? LastBorrow { get; private set; }
        private Task RecordAsync(PackageStoreOperationBorrow borrow)
        {
            ScopedCalls++;
            LastBorrow = borrow;
            borrow.ValidateForInstallPath("packages/example/1.0.0");
            return callback?.Invoke(borrow) ?? Task.CompletedTask;
        }
        public Task OnPackagesChangingAsync(PackageChangeSet changes, PackageStoreOperationBorrow borrow,
            CancellationToken ct) => RecordAsync(borrow);
        public Task OnPackagesChangedAsync(PackageChangeSet changes, PackageStoreOperationBorrow borrow,
            CancellationToken ct) => RecordAsync(borrow);
        public Task OnPackageFailedAsync(string packageId, Exception exception, PackageStoreOperationBorrow borrow,
            CancellationToken ct) => RecordAsync(borrow);
        public Task OnPackagesReconciledAsync(PackageChangeSet changes, IReadOnlyList<ResolvedPackage> packages,
            PackageStoreOperationBorrow borrow, CancellationToken ct) => RecordAsync(borrow);
    }
}
