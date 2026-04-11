using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using System.Runtime.Versioning;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyProviderTests : IDisposable
{
    private const string PackageId = "Nuplane.Loading";
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-assembly-provider-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public void GetAssemblies_WhenContextMissing_ReturnsEmpty()
    {
        var sut = new PackageAssemblyProvider(new PackageLoader());

        var assemblies = sut.GetAssemblies(PackageId, "1.0.0");

        Assert.Empty(assemblies);
    }

    [Fact]
    public async Task GetAssemblies_WhenPackageContainsAdditionalManagedAssemblies_LoadsAndReturnsAllCandidateAssemblies()
    {
        var installPath = CreatePackageInstallPath([
            typeof(PackageLoader).Assembly.Location,
            typeof(IPackageAssemblyProvider).Assembly.Location
        ]);
        var loader = new PackageLoader();

        await loader.EnsureLoadedAsync(
            [new ResolvedPackage(PackageId, "1.0.0", "feed-a", installPath, DateTimeOffset.UtcNow, "source-a")],
            [],
            CancellationToken.None);

        var sut = new PackageAssemblyProvider(loader);

        var assemblies = sut.GetAssemblies(PackageId, "1.0.0");

        Assert.Contains(assemblies, assembly => string.Equals(assembly.GetName().Name, "Nuplane.Loading", StringComparison.Ordinal));
        Assert.Contains(assemblies, assembly => string.Equals(assembly.GetName().Name, "Nuplane.Loading.Abstractions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAssemblies_WhenCandidateCannotBeMaterialized_SkipsCandidateAndContinues_AndLogsWarning()
    {
        var installPath = CreatePackageInstallPath([typeof(PackageLoader).Assembly.Location]);
        File.WriteAllText(Path.Combine(installPath, "not-a-managed-assembly.dll"), "not a managed assembly");
        var loader = new PackageLoader();

        await loader.EnsureLoadedAsync(
            [new ResolvedPackage(PackageId, "2.0.0", "feed-a", installPath, DateTimeOffset.UtcNow, "source-a")],
            [],
            CancellationToken.None);

        var logger = new CaptureLogger<PackageAssemblyProvider>();
        var sut = new PackageAssemblyProvider(loader, logger);

        var assemblies = sut.GetAssemblies(PackageId, "2.0.0");

        Assert.Contains(assemblies, assembly => string.Equals(assembly.GetName().Name, "Nuplane.Loading", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.LogLevel == LogLevel.Warning &&
            entry.Message.Contains("Skipping assembly candidate", StringComparison.Ordinal) &&
            entry.Message.Contains("not-a-managed-assembly.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAssemblies_ForMultiTargetPackage_ReturnsOnlyAssembliesFromSelectedFrameworkAssetSet()
    {
        var selectedFramework = GetHostFrameworkFolderName();
        var otherFramework = GetAlternateFrameworkFolderName(selectedFramework);
        var installPath = CreateMultiTargetPackageInstallPath(
            [
                (selectedFramework, [PackageId, "host-helper"]),
                (otherFramework, [PackageId, "other-helper"])
            ]);
        var loader = new PackageLoader();

        await loader.EnsureLoadedAsync(
            [new ResolvedPackage(PackageId, "3.0.0", "feed-a", installPath, DateTimeOffset.UtcNow, "source-a")],
            [],
            CancellationToken.None);

        var sut = new PackageAssemblyProvider(loader);

        var assemblies = sut.GetAssemblies(PackageId, "3.0.0");

        Assert.NotEmpty(assemblies);
        Assert.All(assemblies, assembly =>
        {
            Assert.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{selectedFramework}{Path.DirectorySeparatorChar}", assembly.Location, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{otherFramework}{Path.DirectorySeparatorChar}", assembly.Location, StringComparison.OrdinalIgnoreCase);
        });
    }

    private string CreatePackageInstallPath(IReadOnlyList<string> sourcePaths)
    {
        var destinationDirectory = _tempDir.CreateSubdirectory(Guid.NewGuid().ToString("N"));
        foreach (var sourcePath in sourcePaths)
        {
            var destinationPath = Path.Combine(destinationDirectory.FullName, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return destinationDirectory.FullName;
    }

    private string CreateMultiTargetPackageInstallPath(
        IReadOnlyList<(string Framework, IReadOnlyList<string> AssemblyNames)> frameworks)
    {
        var packageRoot = _tempDir.CreateSubdirectory(Guid.NewGuid().ToString("N"));
        foreach (var (framework, assemblyNames) in frameworks)
        {
            var frameworkDirectory = Directory.CreateDirectory(Path.Combine(packageRoot.FullName, "lib", framework));
            foreach (var assemblyName in assemblyNames)
            {
                File.Copy(typeof(PackageLoader).Assembly.Location, Path.Combine(frameworkDirectory.FullName, $"{assemblyName}.dll"), overwrite: true);
            }
        }

        return packageRoot.FullName;
    }

    private static string GetHostFrameworkFolderName()
    {
        var attribute = typeof(PackageAssemblyProviderTests).Assembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .OfType<TargetFrameworkAttribute>()
            .Single();

        var frameworkName = new FrameworkName(attribute.FrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETStandard" => $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETFramework" => $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}",
            _ => throw new InvalidOperationException($"Unsupported test host framework '{attribute.FrameworkName}'.")
        };
    }

    private static string GetAlternateFrameworkFolderName(string hostFrameworkFolder)
    {
        if (hostFrameworkFolder.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(hostFrameworkFolder, "netstandard2.0", StringComparison.OrdinalIgnoreCase)
                ? "netstandard1.0"
                : "netstandard2.0";
        }

        if (hostFrameworkFolder.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && hostFrameworkFolder.Contains('.', StringComparison.Ordinal))
        {
            var versionText = hostFrameworkFolder[3..];
            if (Version.TryParse(versionText, out var version))
            {
                var alternateMajor = version.Major > 1 ? version.Major - 1 : version.Major + 1;
                return $"net{alternateMajor}.{version.Minor}";
            }
        }

        return string.Equals(hostFrameworkFolder, "net472", StringComparison.OrdinalIgnoreCase)
            ? "net45"
            : "net472";
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

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

