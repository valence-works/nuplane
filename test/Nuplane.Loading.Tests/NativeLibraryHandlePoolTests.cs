using System.Runtime.CompilerServices;

namespace Nuplane.Loading.Tests;

public sealed class NativeLibraryHandlePoolTests
{
    [Fact]
    public async Task Load_CachesExactPathAndDisposeFreesEachHandleOnce()
    {
        var loadCalls = 0;
        var freedHandles = new List<IntPtr>();
        var pool = new NativeLibraryHandlePool(
            _ => new IntPtr(Interlocked.Increment(ref loadCalls)),
            freedHandles.Add);

        var first = pool.Load("/packages/native/probe.dll");
        var repeated = pool.Load("/packages/native/probe.dll");
        var secondPath = pool.Load("/packages/native/other.dll");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, secondPath);
        Assert.Equal(2, loadCalls);

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Equal([first, secondPath], freedHandles);
        Assert.Throws<ObjectDisposedException>(() => pool.Load("/packages/native/late.dll"));
    }

    [Fact]
    public async Task DisposeAsync_WhenFreeFails_KeepsFailureStickyAndDoesNotRetry()
    {
        var failure = new IOException("native release outcome is unknown");
        var freeCalls = 0;
        var pool = new NativeLibraryHandlePool(
            static _ => new IntPtr(1),
            _ =>
            {
                Interlocked.Increment(ref freeCalls);
                throw failure;
            });
        pool.Load("/packages/native/probe.dll");

        var first = await Assert.ThrowsAsync<IOException>(() => pool.DisposeAsync().AsTask());
        var second = await Assert.ThrowsAsync<IOException>(() => pool.DisposeAsync().AsTask());

        Assert.Same(failure, first);
        Assert.Same(first, second);
        Assert.Equal(1, freeCalls);
        Assert.Throws<ObjectDisposedException>(() => pool.Load("/packages/native/late.dll"));
    }

    [Fact]
    public async Task UnleasedCollectibleLifetime_ReleasesNativeHandlesAfterLongWeakDeath()
    {
        var freed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new NativeLibraryHandlePool(static _ => new IntPtr(1), _ => freed.TrySetResult(true));
        pool.Load("/packages/native/probe.dll");
        var weakLifetime = RetainAndDropLifetime(pool);

        for (var attempt = 0; attempt < 80 && !freed.Task.IsCompleted; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }

        await freed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(weakLifetime.TryGetTarget(out _));
    }

    [Fact]
    public async Task UnleasedCollectibleLifetime_RetainsNativeHandlesUntilFinalizationCompletes()
    {
        var freed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new NativeLibraryHandlePool(static _ => new IntPtr(1), _ => freed.TrySetResult(true));
        pool.Load("/packages/native/probe.dll");
        var (weakLifetime, finalizationGate) = RetainAndDropFinalizableLifetime(pool);

        try
        {
            GC.Collect();
            await finalizationGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.True(weakLifetime.TryGetTarget(out _));
            Assert.False(freed.Task.IsCompleted);
        }
        finally
        {
            finalizationGate.AllowFinalization();
        }

        await finalizationGate.Completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        for (var attempt = 0; attempt < 80 && !freed.Task.IsCompleted; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }

        await freed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(weakLifetime.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> RetainAndDropLifetime(NativeLibraryHandlePool pool)
    {
        var lifetime = new object();
        var weakLifetime = new WeakReference<object>(lifetime, trackResurrection: true);
        PackageNativeLibraryLifetimeRetention.Retain(lifetime, pool, isCollectible: true);
        return weakLifetime;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object> WeakLifetime, FinalizationGate Gate) RetainAndDropFinalizableLifetime(
        NativeLibraryHandlePool pool)
    {
        var gate = new FinalizationGate();
        var lifetime = new FinalizableLifetime(gate);
        var weakLifetime = new WeakReference<object>(lifetime, trackResurrection: true);
        PackageNativeLibraryLifetimeRetention.Retain(lifetime, pool, isCollectible: true);
        return (weakLifetime, gate);
    }

    private sealed class FinalizationGate
    {
        private readonly TaskCompletionSource<bool> _allowFinalization =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void BlockFinalizer()
        {
            Started.TrySetResult(true);
            _allowFinalization.Task.GetAwaiter().GetResult();
            Completed.TrySetResult(true);
        }

        internal void AllowFinalization() => _allowFinalization.TrySetResult(true);
    }

    private sealed class FinalizableLifetime(FinalizationGate gate)
    {
        ~FinalizableLifetime() => gate.BlockFinalizer();
    }
}
