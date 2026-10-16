namespace Nuplane.Loading.Tests;

internal static class CollectibleContextTestLifetime
{
    internal static void CollectUntilGone(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(25);
        }
    }
}
