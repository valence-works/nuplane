using System.Reflection;
using System.Runtime.CompilerServices;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyLoadContextTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-alc-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public void LoadFromAssemblyName_AssemblyInContext_LoadSucceeds()
    {
        var path = CopyFixtureAssembly("alc-load");
        var ctx = new PackageAssemblyLoadContext(path, [], new());
        var name = AssemblyName.GetAssemblyName(path);

        var assembly = ctx.LoadFromAssemblyName(name);

        Assert.NotNull(assembly);
        ctx.Unload();
    }

    [Fact]
    public void Unload_AfterForcedGC_ALCIsCollectible()
    {
        var path = CopyFixtureAssembly("alc-collect");
        var weakRef = CreateAndUnload(path);

        CollectUntilGone(weakRef);

        Assert.False(weakRef.IsAlive, "AssemblyLoadContext should be collected after Unload() and forced GC.");
    }

    [Fact]
    public void Ctor_MainAssemblyPath_ResolvesPackageName()
    {
        var path = CopyFixtureAssembly("alc-name");
        var ctx = new PackageAssemblyLoadContext(path, [], new());

        Assert.Contains("nuplane:", ctx.Name, StringComparison.OrdinalIgnoreCase);

        ctx.Unload();
    }

    [Fact]
    public void Unload_CalledTwice_DoesNotThrow()
    {
        var path = CopyFixtureAssembly("alc-double-unload");
        var ctx = new PackageAssemblyLoadContext(path, [], new());

        ctx.Unload();
        var ex = Record.Exception(() => ctx.Unload());

        Assert.Null(ex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndUnload(string path)
    {
        var ctx = new PackageAssemblyLoadContext(path, [], new());
        var weakRef = new WeakReference(ctx, trackResurrection: true);
        ctx.Unload();
        return weakRef;
    }

    private static void CollectUntilGone(WeakReference weakRef)
    {
        for (var i = 0; i < 20 && weakRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(25);
        }
    }

    private string CopyFixtureAssembly(string subDirName)
    {
        var dir = _tempDir.CreateSubdirectory(subDirName);
        var src = typeof(FixtureMarker).Assembly.Location;
        var dest = Path.Combine(dir.FullName, Path.GetFileName(src));
        File.Copy(src, dest);
        return dest;
    }
}
