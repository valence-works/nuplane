using Microsoft.Extensions.Logging;

namespace Nuplane.Loading.Tests;

public sealed class PackageMetadataLoadModeAdvisorTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-metadata-advisor-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public async Task EvaluateAsync_WhenPackageDeclaresHostIntegratedDependencyClosure_ReturnsPackageMetadataResult()
    {
        var messages = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(messages)));
        var installPath = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
        PackageMetadataTestSupport.WriteMetadata(
            installPath,
            PackageLoadMode.HostIntegrated,
            LoadModeScopes.DependencyClosure,
            "Requires framework type resolution.");
        var package = PackageMetadataTestSupport.Package("pkg-a", "1.0.0", installPath);
        var context = new LoadModeAdvisorContext(
            "graph:pkg-a@1.0.0",
            [package],
            PackageLoadModeSelectionPolicy.Automatic,
            PackageLoadMode.Collectible,
            new Dictionary<string, PackageLoadMode>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageMetadataLoadModeAdvisor(
            new PackageMetadataLoadModeReader(),
            loggerFactory.CreateLogger<PackageMetadataLoadModeAdvisor>());

        var results = await sut.EvaluateAsync(context, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("package-metadata", result.AdvisorName);
        Assert.Equal("pkg-a", result.PackageId);
        Assert.Equal(PackageLoadMode.HostIntegrated, result.RequestedLoadMode);
        Assert.Equal(LoadModeScopes.DependencyClosure, result.Scope);
        Assert.Equal(LoadModeReasonCodes.PackageMetadata, result.ReasonCode);
        Assert.True(result.IsValid);
        Assert.Contains(messages, message =>
            message.Contains("Discovered package load metadata", StringComparison.Ordinal)
            && message.Contains("pkg-a@1.0.0", StringComparison.Ordinal)
            && message.Contains("HostIntegrated", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
