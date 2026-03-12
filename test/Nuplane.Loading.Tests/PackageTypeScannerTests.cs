using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.Extensions.Logging;
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
    public void FindTypes_WhenAssemblyHasMissingDependency_SkipsUninspectableAssemblyWithoutThrowing_AndLogsWarning()
    {
        var brokenAssemblyPath = CopyBrokenCandidateAssembly("broken-package");
        var ctx = new PackageAssemblyLoadContext(brokenAssemblyPath, [], new SharedAssemblyPolicyMatcher());
        var logger = new CaptureLogger<PackageTypeScanner>();

        try
        {
            ctx.LoadFromAssemblyName(new AssemblyName(BrokenCandidateAssemblyName));

            var sut = CreateScanner("pkg-broken", "1.0.0", ctx, logger);

            var discovered = sut.FindTypes(typeof(object), "pkg-broken", "1.0.0");

            Assert.Empty(discovered);
            Assert.Contains(logger.Entries, entry =>
                entry.LogLevel == LogLevel.Warning &&
                entry.Message.Contains("Skipping assembly", StringComparison.Ordinal) &&
                entry.Message.Contains("pkg-broken@1.0.0", StringComparison.Ordinal));
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void FindTypes_WhenEarlierAssemblyInspectionFails_ContinuesScanningLaterAssemblies_AndLogsWarning()
    {
        var brokenAssemblyPath = CopyBrokenCandidateAssembly("multi-assembly-package");
        var healthyAssemblyPath = CopyAssembly(typeof(HealthyFixtureType).Assembly, "multi-assembly-package");
        var ctx = new PackageAssemblyLoadContext(brokenAssemblyPath, [], new SharedAssemblyPolicyMatcher());
        var logger = new CaptureLogger<PackageTypeScanner>();

        try
        {
            ctx.LoadFromAssemblyName(new AssemblyName(BrokenCandidateAssemblyName));
            ctx.LoadFromAssemblyPath(healthyAssemblyPath);

            var sut = CreateScanner("pkg-mixed", "2.0.0", ctx, logger);

            var discovered = sut.FindTypes(typeof(object), "pkg-mixed", "2.0.0");

            Assert.Contains(discovered, type => type.FullName == typeof(HealthyFixtureType).FullName);
            Assert.Contains(logger.Entries, entry =>
                entry.LogLevel == LogLevel.Warning &&
                entry.Message.Contains("Skipping assembly", StringComparison.Ordinal) &&
                entry.Message.Contains("pkg-mixed@2.0.0", StringComparison.Ordinal));
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void PartialScanWarning_IncludesFirstLoaderExceptionMessage()
    {
        const string firstLoaderExceptionMessage = "Could not load file or assembly 'Elsa.Common, Version=3.6.0.0, Culture=neutral, PublicKeyToken=null'.";
        var logger = new CaptureLogger<PackageTypeScanner>();
        var sut = CreateScanner("pkg-partial", "3.0.0", new TestAssemblyLoadContext(), logger);
        var getCandidateTypes = typeof(PackageTypeScanner).GetMethod("GetCandidateTypes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(getCandidateTypes);

        var assembly = new PartialScanAssembly(
            "Nuplane.Tests.PartialScanAssembly",
            new ReflectionTypeLoadException(
                [typeof(HealthyFixtureType), null],
                [new FileNotFoundException(firstLoaderExceptionMessage)]));

        var discovered = (IReadOnlyList<Type>)getCandidateTypes!.Invoke(sut, [assembly, "pkg-partial", "3.0.0"])!;

        Assert.Single(discovered);
        Assert.Same(typeof(HealthyFixtureType), discovered[0]);
        Assert.Contains(logger.Entries, entry =>
            entry.LogLevel == LogLevel.Warning &&
            entry.Message.Contains("Partially scanned assembly", StringComparison.Ordinal) &&
            entry.Message.Contains("pkg-partial@3.0.0", StringComparison.Ordinal) &&
            entry.Message.Contains(firstLoaderExceptionMessage, StringComparison.Ordinal));
    }

    private PackageTypeScanner CreateScanner(
        string packageId,
        string version,
        AssemblyLoadContext ctx,
        ILogger<PackageTypeScanner>? logger = null)
    {
        var loader = new TestPackageLoader(packageId, version, ctx);
        return new PackageTypeScanner(loader, logger);
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

    private sealed class TestAssemblyLoadContext : AssemblyLoadContext;

    private sealed class PartialScanAssembly(string assemblyName, ReflectionTypeLoadException exception) : Assembly
    {
        public override string FullName => assemblyName;

        public override AssemblyName GetName(bool copiedName) => new(assemblyName);

        public override Type[] GetExportedTypes() => throw exception;

        public override string Location => string.Empty;

        public override IEnumerable<CustomAttributeData> CustomAttributes => [];

        public override IList<CustomAttributeData> GetCustomAttributesData() => [];

        public override Module ManifestModule => throw new NotSupportedException();

        public override Module? GetModule(string name) => null;

        public override Module[] GetModules(bool getResourceModules) => [];

        public override AssemblyName[] GetReferencedAssemblies() => [];

        public override Type? GetType(string name, bool throwOnError, bool ignoreCase) => null;

        public override Type[] GetTypes() => throw new NotSupportedException();

        public override object[] GetCustomAttributes(bool inherit) => [];

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => [];

        public override bool IsDefined(Type attributeType, bool inherit) => false;

        public override Stream? GetManifestResourceStream(string name) => null;

        public override string[] GetManifestResourceNames() => [];

        public override FileStream? GetFile(string name) => null;

        public override FileStream[] GetFiles(bool getResourceModules) => [];

        public override Module[] GetLoadedModules(bool getResourceModules) => [];

        public override string ImageRuntimeVersion => string.Empty;

        public override bool ReflectionOnly => false;

        public override string ToString() => assemblyName;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
