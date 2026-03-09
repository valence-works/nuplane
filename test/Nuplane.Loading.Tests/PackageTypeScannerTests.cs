using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageTypeScannerTests : IDisposable
{
    private const string BrokenCandidateAssemblyName = "Nuplane.Loading.Tests.Fixtures.BrokenCandidate";
    private const string OriginalBrokenDependencyAssemblyName = "Nuplane.Loading.Tests.Fixtures.BrokenDependency";
    private const string PatchedMissingDependencyAssemblyName = "Nuplane.Loading.Tests.Fixtures.BrokenDependencz";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-scanner-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public void FindTypes_WhenAssemblyHasMissingDependency_SkipsUninspectableAssemblyWithoutThrowing()
    {
        var brokenAssemblyPath = CopyBrokenCandidateAssembly("broken-package");
        var ctx = new PackageAssemblyLoadContext(brokenAssemblyPath, [], new SharedAssemblyPolicyMatcher());

        try
        {
            ctx.LoadFromAssemblyName(new AssemblyName(BrokenCandidateAssemblyName));

            var sut = CreateScanner("pkg-broken", "1.0.0", ctx);

            var discovered = sut.FindTypes(typeof(object), "pkg-broken", "1.0.0");

            Assert.Empty(discovered);
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void FindTypes_WhenEarlierAssemblyInspectionFails_ContinuesScanningLaterAssemblies()
    {
        var brokenAssemblyPath = CopyBrokenCandidateAssembly("multi-assembly-package");
        var healthyAssemblyPath = CopyAssembly(typeof(HealthyFixtureType).Assembly, "multi-assembly-package");
        var ctx = new PackageAssemblyLoadContext(brokenAssemblyPath, [], new SharedAssemblyPolicyMatcher());

        try
        {
            ctx.LoadFromAssemblyName(new AssemblyName(BrokenCandidateAssemblyName));
            ctx.LoadFromAssemblyPath(healthyAssemblyPath);

            var sut = CreateScanner("pkg-mixed", "2.0.0", ctx);

            var discovered = sut.FindTypes(typeof(object), "pkg-mixed", "2.0.0");

            Assert.Contains(discovered, type => type.FullName == typeof(HealthyFixtureType).FullName);
        }
        finally
        {
            ctx.Unload();
        }
    }

    private PackageTypeScanner CreateScanner(string packageId, string version, AssemblyLoadContext ctx)
    {
        var loader = new TestPackageLoader(packageId, version, ctx);
        return new PackageTypeScanner(loader);
    }

    private static string GetBuiltFixtureAssemblyPath(string assemblyName) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../{assemblyName}/bin/Debug/net10.0/{assemblyName}.dll"));

    private string CopyBrokenCandidateAssembly(string subdirectory)
    {
        var assemblyPath = CopyAssemblyCore(GetBuiltFixtureAssemblyPath(BrokenCandidateAssemblyName), subdirectory, referencedAssemblies: []);
        PatchAssemblyReferenceName(assemblyPath, OriginalBrokenDependencyAssemblyName, PatchedMissingDependencyAssemblyName);
        return assemblyPath;
    }

    private string CopyAssembly(Assembly assembly, string subdirectory)
        => CopyAssemblyCore(assembly.Location, subdirectory, assembly.GetReferencedAssemblies());

    private string CopyAssemblyCore(string sourcePath, string subdirectory, IReadOnlyList<AssemblyName> referencedAssemblies)
    {
        var destinationDirectory = _tempDir.CreateSubdirectory(subdirectory);
        var destinationPath = Path.Combine(destinationDirectory.FullName, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, overwrite: true);

        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        foreach (var dependencyName in referencedAssemblies)
        {
            var dependencyPath = Path.Combine(sourceDirectory, dependencyName.Name + ".dll");
            if (!File.Exists(dependencyPath))
            {
                continue;
            }

            var dependencyDestinationPath = Path.Combine(destinationDirectory.FullName, Path.GetFileName(dependencyPath));
            if (!File.Exists(dependencyDestinationPath))
            {
                File.Copy(dependencyPath, dependencyDestinationPath, overwrite: true);
            }
        }

        return destinationPath;
    }

    private static void PatchAssemblyReferenceName(string assemblyPath, string originalName, string replacementName)
    {
        if (originalName.Length != replacementName.Length)
        {
            throw new InvalidOperationException("Patched assembly reference names must be the same length.");
        }

        var originalBytes = Encoding.UTF8.GetBytes(originalName);
        var replacementBytes = Encoding.UTF8.GetBytes(replacementName);
        var assemblyBytes = File.ReadAllBytes(assemblyPath);
        var replaced = false;

        for (var index = 0; index <= assemblyBytes.Length - originalBytes.Length; index++)
        {
            if (!assemblyBytes.AsSpan(index, originalBytes.Length).SequenceEqual(originalBytes))
            {
                continue;
            }

            replacementBytes.CopyTo(assemblyBytes.AsSpan(index, replacementBytes.Length));
            replaced = true;
        }

        if (!replaced)
        {
            throw new InvalidOperationException($"Could not patch assembly reference '{originalName}' in '{assemblyPath}'.");
        }

        File.WriteAllBytes(assemblyPath, assemblyBytes);
    }

    private sealed class TestPackageLoader(string packageId, string version, AssemblyLoadContext context) : IPackageLoader
    {
        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool TryRemoveContext(string requestedPackageId, string requestedVersion, out PackageLoadContextHandle? handle)
        {
            handle = null;
            return false;
        }

        public bool TryGetContext(string requestedPackageId, string requestedVersion, out PackageLoadContextHandle? handle)
        {
            if (requestedPackageId == packageId && requestedVersion == version)
            {
                handle = new PackageLoadContextHandle($"{packageId}@{version}", context);
                return true;
            }

            handle = null;
            return false;
        }
    }
}
