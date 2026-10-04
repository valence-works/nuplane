using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Proves source precedence through the complete host, directory watcher, resolver, and store path.
/// </summary>
public sealed class DesiredSourcePrecedenceIntegrationTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] PackageIds = ["Activities", "Renewals"];

    [Fact]
    public async Task SourcePriorities_PreferSharedUpdateFeed_AcrossTwoRunningHosts()
    {
        await using var fixture = await PrecedenceFixture.StartAsync(useSourcePriorities: true, hostCount: 2);

        foreach (var host in fixture.Hosts)
        {
            var state = await host.ReadStateAsync();
            Assert.Equal(PackageIds.Length, state.ActiveVersionById.Count);
            Assert.All(PackageIds, packageId =>
            {
                Assert.Equal("1.0.0", state.ActiveVersionById[packageId]);
                Assert.True(state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor));
                Assert.Equal("renewal-demo-baseline", descriptor.SourceName);
                Assert.Equal("renewal-demo-baseline", descriptor.FeedName);
                Assert.Equal("1.0.0", descriptor.Version);
                Assert.Equal(
                    $"baseline-{host.Name}-1.0.0",
                    File.ReadAllText(Path.Combine(descriptor.InstallPath, "content", $"{packageId}.txt")));
            });
        }

        fixture.PublishUpdatePackages();

        foreach (var host in fixture.Hosts)
        {
            await WaitForStateAsync(
                host,
                static current => PackageIds.All(packageId =>
                    current.ActiveVersionById.TryGetValue(packageId, out var version)
                    && version == "1.1.0"));

            var state = await host.ReadStateAsync();

            Assert.Equal(PackageIds.Length, state.ActiveVersionById.Count);
            Assert.All(PackageIds, packageId =>
            {
                Assert.Equal("1.1.0", state.ActiveVersionById[packageId]);
                Assert.True(state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor));
                Assert.Equal("renewal-demo-updates", descriptor.SourceName);
                Assert.Equal("renewal-demo-updates", descriptor.FeedName);
                Assert.Equal("1.1.0", descriptor.Version);
                Assert.Equal(
                    $"update-{packageId}-1.1.0",
                    File.ReadAllText(Path.Combine(descriptor.InstallPath, "content", $"{packageId}.txt")));
            });

            var aggregate = await host.AggregateDesiredStateAsync();
            Assert.Equal(PackageIds, aggregate.Requests.Select(static request => request.Id));
            Assert.All(aggregate.Requests, request =>
            {
                Assert.Equal("1.1.0", request.VersionRange);
                Assert.Equal("renewal-demo-updates", request.FeedName);
                Assert.Equal("renewal-demo-updates", request.SourceName);
                Assert.Equal(PackageUpdatePolicy.Exact, request.UpdatePolicy);
            });

            // Loading is a separate module and is intentionally absent from this host composition.
            Assert.Null(host.Host.Services.GetService<IPackageAssemblyCatalog>());
        }
    }

    [Fact]
    public async Task NoSourcePriority_ExplicitReconciliation_PreservesAlphabeticalBaseline()
    {
        await using var fixture = await PrecedenceFixture.StartAsync(
            useSourcePriorities: false,
            hostCount: 1,
            watchFeeds: false);

        fixture.PublishUpdatePackages();

        var host = Assert.Single(fixture.Hosts);
        var result = await host.Host.Services
            .GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Empty(result.FailedPackages);
        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);

        var state = await host.ReadStateAsync();
        Assert.Equal(PackageIds.Length, state.ActiveVersionById.Count);
        Assert.All(PackageIds, packageId =>
        {
            Assert.Equal("1.0.0", state.ActiveVersionById[packageId]);
            Assert.True(state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor));
            Assert.Equal("renewal-demo-baseline", descriptor.SourceName);
            Assert.Equal("renewal-demo-baseline", descriptor.FeedName);
            Assert.Equal("1.0.0", descriptor.Version);
        });
    }

    [Fact]
    public async Task HigherPriorityCorruptPackage_ExplicitReconciliation_KeepsActiveLastKnownGood()
    {
        await using var fixture = await PrecedenceFixture.StartAsync(
            useSourcePriorities: true,
            hostCount: 1,
            watchFeeds: false);

        fixture.PublishCorruptUpdatePackage("Renewals", "1.1.0");

        var host = Assert.Single(fixture.Hosts);
        var result = await host.Host.Services
            .GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual(), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);
        Assert.Contains("Renewals", result.FailedPackages);

        var state = await host.ReadStateAsync();
        Assert.Equal("1.0.0", state.ActiveVersionById["Renewals"]);
        Assert.Equal("1.0.0", state.LastKnownGoodById["Renewals"]);
        Assert.Contains("Renewals", state.LastFailureById.Keys);
        Assert.True(state.ActivePackageDescriptorsByIdNormalized.TryGetValue("Renewals", out var descriptor));
        Assert.Equal("renewal-demo-baseline", descriptor.SourceName);
        Assert.Equal("renewal-demo-baseline", descriptor.FeedName);
        Assert.Equal("1.0.0", descriptor.Version);
        Assert.Equal(
            "baseline-host-1-1.0.0",
            File.ReadAllText(Path.Combine(descriptor.InstallPath, "content", "Renewals.txt")));
    }

    private static async Task<StoreStateRecord> WaitForStateAsync(
        PrecedenceHost host,
        Func<StoreStateRecord, bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        StoreStateRecord? lastState = null;

        while (DateTime.UtcNow < deadline)
        {
            lastState = await host.ReadLiveStateAsync();
            if (predicate(lastState))
            {
                return lastState;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"Timed out waiting for host '{host.Name}' to reach the expected store state. "
            + $"Last active versions: {string.Join(", ", lastState?.ActiveVersionById.Select(pair => $"{pair.Key}={pair.Value}") ?? [])}.");
    }

    private static void BuildNupkgTo(
        string filePath,
        string packageId,
        string version,
        string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");
            using (var writer = new StreamWriter(nuspecEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata>
                        <id>{packageId}</id>
                        <version>{version}</version>
                        <authors>nuplane-integration-tests</authors>
                        <description>Nuplane source precedence integration fixture.</description>
                      </metadata>
                    </package>
                    """);
            }

            var contentEntry = archive.CreateEntry($"content/{packageId}.txt");
            using var contentWriter = new StreamWriter(contentEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            contentWriter.Write(content);
        }

        File.WriteAllBytes(filePath, stream.ToArray());
    }

    private sealed class PrecedenceFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _updatesDirectory;

        private PrecedenceFixture(string root, string updatesDirectory, IReadOnlyList<PrecedenceHost> hosts)
        {
            _root = root;
            _updatesDirectory = updatesDirectory;
            Hosts = hosts;
        }

        public IReadOnlyList<PrecedenceHost> Hosts { get; }

        public static async Task<PrecedenceFixture> StartAsync(
            bool useSourcePriorities,
            int hostCount,
            bool watchFeeds = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"nuplane-source-precedence-{Guid.NewGuid():N}");
            var updatesDirectory = Path.Combine(root, "shared-updates");
            Directory.CreateDirectory(updatesDirectory);

            var hosts = new List<PrecedenceHost>(hostCount);
            try
            {
                for (var index = 0; index < hostCount; index++)
                {
                    var name = $"host-{index + 1}";
                    var baselineDirectory = Path.Combine(root, $"{name}-baseline");
                    var installRoot = Path.Combine(root, $"{name}-packages");
                    var stateFilePath = Path.Combine(root, $"{name}-store-state.json");

                    foreach (var packageId in PackageIds)
                    {
                        BuildNupkgTo(
                            Path.Combine(baselineDirectory, $"{packageId}.1.0.0.nupkg"),
                            packageId,
                            "1.0.0",
                            $"baseline-{name}-1.0.0");
                    }

                    var host = PrecedenceHost.Create(
                        name,
                        baselineDirectory,
                        updatesDirectory,
                        installRoot,
                        stateFilePath,
                        useSourcePriorities,
                        watchFeeds);
                    hosts.Add(host);
                    using var startTimeout = new CancellationTokenSource(WaitTimeout);
                    await host.Host.StartAsync(startTimeout.Token);
                    if (watchFeeds)
                    {
                        await host.WaitUntilWatchingUpdatesAsync(updatesDirectory);
                    }
                }

                return new(root, updatesDirectory, hosts);
            }
            catch
            {
                foreach (var host in hosts.AsEnumerable().Reverse())
                {
                    await host.DisposeAsync();
                }

                TryDeleteDirectory(root);
                throw;
            }
        }

        public void PublishUpdatePackages()
        {
            foreach (var packageId in PackageIds)
            {
                var stagingPath = Path.Combine(_updatesDirectory, $".{packageId}.{Guid.NewGuid():N}.tmp");
                BuildNupkgTo(
                    stagingPath,
                    packageId,
                    "1.1.0",
                    $"update-{packageId}-1.1.0");

                var packagePath = Path.Combine(_updatesDirectory, $"{packageId}.1.1.0.nupkg");
                // Publish only after the complete ZIP is closed, so each watcher observes a valid
                // package at the final .nupkg path.
                File.Move(stagingPath, packagePath);
            }

        }

        public void PublishCorruptUpdatePackage(string packageId, string version)
        {
            var packagePath = Path.Combine(_updatesDirectory, $"{packageId}.{version}.nupkg");
            var stagingPath = Path.Combine(_updatesDirectory, $".{packageId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(stagingPath, "this is not a ZIP package", Encoding.UTF8);
            File.Move(stagingPath, packagePath);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var host in Hosts.AsEnumerable().Reverse())
            {
                await host.DisposeAsync();
            }

            TryDeleteDirectory(_root);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup must not mask the assertion that failed the test.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup must not mask the assertion that failed the test.
            }
        }
    }

    private sealed class PrecedenceHost : IAsyncDisposable
    {
        private readonly WatcherLogProvider _watcherLogs;

        private PrecedenceHost(
            string name,
            string stateFilePath,
            IHost host,
            WatcherLogProvider watcherLogs)
        {
            Name = name;
            StateFilePath = stateFilePath;
            Host = host;
            _watcherLogs = watcherLogs;
        }

        public string Name { get; }
        public string StateFilePath { get; }
        public IHost Host { get; }

        public static PrecedenceHost Create(
            string name,
            string baselineDirectory,
            string updatesDirectory,
            string installRoot,
            string stateFilePath,
            bool useSourcePriorities,
            bool watchFeeds)
        {
            var watcherLogs = new WatcherLogProvider();
            var configurationValues = new Dictionary<string, string?>
            {
                ["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = "false",
                ["Nuplane:Reconciliation:MaxRetryAttempts"] = "0",
                ["Nuplane:FeedResolution:OfflineMode"] = "true",
                ["Nuplane:FeedResolution:PackageInstallRoot"] = installRoot,
                ["Nuplane:FeedResolution:FeedPriorities:renewal-demo-baseline"] = "0",
                ["Nuplane:FeedResolution:FeedPriorities:renewal-demo-updates"] = "100",
                ["Nuplane:StoreRegistry:StateFilePath"] = stateFilePath
            };

            if (useSourcePriorities)
            {
                configurationValues["Nuplane:DesiredState:SourcePriorities:renewal-demo-updates"] = "0";
                configurationValues["Nuplane:DesiredState:SourcePriorities:renewal-demo-baseline"] = "100";
            }

            var host = new HostBuilder()
                .ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(configurationValues))
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.AddProvider(watcherLogs);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddNuplane(context.Configuration.GetSection("Nuplane"), nuplane =>
                    {
                        nuplane.AddDirectoryFeed("renewal-demo-baseline", baselineDirectory, feed =>
                        {
                            feed.Include("Activities").Include("Renewals");
                            feed.Watch = false;
                        });
                        nuplane.AddDirectoryFeed("renewal-demo-updates", updatesDirectory, feed =>
                        {
                            feed.Include("Activities").Include("Renewals");
                            feed.Watch = watchFeeds;
                            feed.DebounceWindow = TimeSpan.FromMilliseconds(100);
                        });
                    });
                })
                .Build();

            return new(name, stateFilePath, host, watcherLogs);
        }

        public async Task WaitUntilWatchingUpdatesAsync(string updatesDirectory)
        {
            const string feedName = "renewal-demo-updates";
            await _watcherLogs.WaitForEnabledAsync(feedName, WaitTimeout);
            var observed = _watcherLogs.WaitForObservedTriggerAsync(feedName, WaitTimeout);
            var probePath = Path.Combine(updatesDirectory, $"Probe.{Name}.0.0.0.nupkg");
            while (!observed.IsCompleted)
            {
                // Retry one ignored package until this host has processed a real shared-feed event.
                // A fixed path avoids accumulating files that each require a stability probe.
                await File.WriteAllBytesAsync(probePath, [0x50, 0x4B], CancellationToken.None);
                await Task.WhenAny(observed, Task.Delay(TimeSpan.FromSeconds(1)));
            }

            await observed;

            // Drain the observed cycle before the single publication under test.
            using var timeout = new CancellationTokenSource(WaitTimeout);
            var drained = await Host.Services.GetRequiredService<IReconciliationTriggerIngress>()
                .EnqueueAndWaitAsync(ReconciliationTrigger.Manual(), timeout.Token);
            Assert.False(drained.Skipped);
            Assert.Empty(drained.FailedPackages);
        }

        public Task<StoreStateRecord> ReadStateAsync() =>
            NuplaneStore.ReadStateAsync(StateFilePath, CancellationToken.None);

        public Task<StoreStateRecord> ReadLiveStateAsync() =>
            Host.Services.GetRequiredService<IStoreRegistry>().GetStateAsync(CancellationToken.None);

        public Task<DesiredAggregateResult> AggregateDesiredStateAsync() =>
            Host.Services.GetRequiredService<IDesiredStateAggregator>().AggregateAsync(
                Host.Services.GetServices<IDesiredPackageSource>(),
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(WaitTimeout);
                await Host.StopAsync(stopTimeout.Token);
            }
            finally
            {
                Host.Dispose();
            }
        }
    }

    private sealed class WatcherLogProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _watcherReady = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _observedTriggers = new(StringComparer.OrdinalIgnoreCase);

        public ILogger CreateLogger(string categoryName) => new WatcherLogger(categoryName, this);

        public Task WaitForEnabledAsync(string feedName, TimeSpan timeout)
        {
            var ready = _watcherReady.GetOrAdd(
                feedName,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            return ready.Task.WaitAsync(timeout);
        }

        public Task WaitForObservedTriggerAsync(string feedName, TimeSpan timeout)
        {
            var observed = _observedTriggers.GetOrAdd(
                feedName,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            return observed.Task.WaitAsync(timeout);
        }

        public void Dispose()
        {
        }

        private void Record(string categoryName, string message)
        {
            if (!categoryName.Equals(
                    "Nuplane.Sources.Directory.Hosting.DirectorySourceReconciliationTriggerHostedService",
                    StringComparison.Ordinal)
                || !message.Contains("Directory watcher enabled for feed '", StringComparison.Ordinal))
            {
                if (categoryName.Equals("Nuplane.Observability.ReconciliationLogger", StringComparison.Ordinal)
                    && message.Contains("TriggerType=ObservedChange", StringComparison.Ordinal))
                {
                    foreach (var feedName in _observedTriggers.Keys)
                    {
                        if (message.Contains($"TriggerSource={feedName}", StringComparison.Ordinal))
                        {
                            _observedTriggers[feedName].TrySetResult();
                        }
                    }
                }

                return;
            }

            var start = message.IndexOf("Directory watcher enabled for feed '", StringComparison.Ordinal)
                + "Directory watcher enabled for feed '".Length;
            var end = message.IndexOf('\'', start);
            if (end > start)
            {
                _watcherReady.GetOrAdd(
                    message[start..end],
                    static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
            }
        }

        private sealed class WatcherLogger(string categoryName, WatcherLogProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Information)
                {
                    provider.Record(categoryName, formatter(state, exception));
                }
            }
        }
    }
}
