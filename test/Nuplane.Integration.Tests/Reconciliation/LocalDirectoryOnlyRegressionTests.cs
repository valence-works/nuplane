using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Feeds;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Versioning;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Regression integration tests verifying that running with only local directory
/// feeds (no remote feeds configured) works end-to-end without unhandled exceptions.
/// </summary>
public sealed class LocalDirectoryOnlyRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-local-reg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDirectoryOnly_ReconciliationCompletes_WithoutException()
    {
        BuildNupkgTo(_tempDir, "MyPlugin", "1.0.0");

        var feedUri = new Uri("file:///" + _tempDir.Replace('\\', '/').TrimStart('/'));
        var localFeed = new FeedDefinition("local-drop", feedUri, FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([
            new("MyPlugin", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source")
        ]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "MyPlugin" } },
            desiredStateAggregator: new DesiredStateAggregator(),
            desiredActualDiffEngine: new DesiredActualDiffEngine(),
            packageResolver: new MultiFeedPackageResolver(
                new OptionsWrapper<FeedResolutionOptions>(feedOpts),
                new(new OptionsWrapper<FeedResolutionOptions>(feedOpts)),
                Substitute.For<IRemotePackageAcquirer>(),
                Substitute.For<IFeedVersionEnumerator>(),
                Substitute.For<IVersionRangeEvaluator>(),
                NullLogger<MultiFeedPackageResolver>.Instance),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        var trigger = ReconciliationTrigger.Observed(FeedObservationOrigin.DirectoryWatcher("local-drop"));
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("MyPlugin", result.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task LocalDirectoryOnly_EmptyDirectory_CompletesSuccessfully()
    {
        Directory.CreateDirectory(_tempDir);

        var feedUri = new Uri("file:///" + _tempDir.Replace('\\', '/').TrimStart('/'));
        var localFeed = new FeedDefinition("local-drop", feedUri, FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new(),
            desiredStateAggregator: new DesiredStateAggregator(),
            desiredActualDiffEngine: new DesiredActualDiffEngine(),
            packageResolver: new MultiFeedPackageResolver(
                new OptionsWrapper<FeedResolutionOptions>(feedOpts),
                new(new OptionsWrapper<FeedResolutionOptions>(feedOpts)),
                Substitute.For<IRemotePackageAcquirer>(),
                Substitute.For<IFeedVersionEnumerator>(),
                Substitute.For<IVersionRangeEvaluator>(),
                NullLogger<MultiFeedPackageResolver>.Instance),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        var trigger = ReconciliationTrigger.Observed(FeedObservationOrigin.DirectoryWatcher("local-drop"));
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Empty(result.ChangeSet.Added);
    }

    [Fact]
    public async Task LocalDirectoryOnly_MultipleTriggers_DoNotPathologicallyFail()
    {
        BuildNupkgTo(_tempDir, "PluginA", "1.0.0");

        var feedUri = new Uri("file:///" + _tempDir.Replace('\\', '/').TrimStart('/'));
        var localFeed = new FeedDefinition("local-drop", feedUri, FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([
            new("PluginA", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source")
        ]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "PluginA" } },
            desiredStateAggregator: new DesiredStateAggregator(),
            desiredActualDiffEngine: new DesiredActualDiffEngine(),
            packageResolver: new MultiFeedPackageResolver(
                new OptionsWrapper<FeedResolutionOptions>(feedOpts),
                new(new OptionsWrapper<FeedResolutionOptions>(feedOpts)),
                Substitute.For<IRemotePackageAcquirer>(),
                Substitute.For<IFeedVersionEnumerator>(),
                Substitute.For<IVersionRangeEvaluator>(),
                NullLogger<MultiFeedPackageResolver>.Instance),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        // Multiple triggers should all succeed
        for (var i = 0; i < 3; i++)
        {
            var trigger = ReconciliationTrigger.Scheduled();
            var result = await service.TriggerAsync(trigger, CancellationToken.None);
            Assert.False(result.Skipped);
        }
    }

    private static void BuildNupkgTo(string directoryPath, string packageId, string version)
    {
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, $"{packageId}.{version}.nupkg");
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");
            using var writer = new StreamWriter(nuspecEntry.Open(), Encoding.UTF8);
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{version}</version>
                    <authors>test</authors>
                    <description>Test package</description>
                  </metadata>
                </package>
                """);
        }
        File.WriteAllBytes(filePath, ms.ToArray());
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
