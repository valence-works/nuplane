using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;

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

