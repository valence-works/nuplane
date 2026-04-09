using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogObservabilityTests
{
    [Fact]
    public async Task GetSnapshotAsync_WhenStale_EmitsLoadingReadLogAndMetrics()
    {
        var messages = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(messages)));
        using var collector = new MetricCollector("nuplane.catalog.loading.read", "nuplane.catalog.degraded.read");
        using var telemetry = new ReconciliationTelemetry();

        var catalog = new LoadingCatalog(
            new StubActivePackageCatalog(CreateSnapshot("pkg-stale", "/packages/pkg-stale")),
            new PackageLoader(),
            new AssemblyScanCandidateProjector(new PackageLoader()),
            new LoadingCatalogRefreshTracker(),
            Options.Create(new LoadingOptions { Enabled = true }),
            new ReconciliationLogger(loggerFactory.CreateLogger<ReconciliationLogger>()),
            new ReconciliationMetrics(telemetry));

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(LoadingCatalogAvailability.Stale, snapshot.Availability);
        Assert.Contains(messages, message =>
            message.Contains("Loading catalog read", StringComparison.Ordinal)
            && message.Contains("Availability=Stale", StringComparison.Ordinal)
            && message.Contains("ReasonCode=loading-stale", StringComparison.Ordinal));

        var loadingRead = Assert.Single(collector.Measurements, measurement =>
            measurement.InstrumentName == "nuplane.catalog.loading.read"
            && measurement.Tags.TryGetValue("reason_code", out var reasonCode)
            && string.Equals(reasonCode, "loading-stale", StringComparison.Ordinal));
        Assert.Equal("Stale", loadingRead.Tags["availability"]);
        Assert.Equal("1", loadingRead.Tags["package_count"]);
        Assert.Equal("loading-stale", loadingRead.Tags["reason_code"]);

        var degradedRead = Assert.Single(collector.Measurements, measurement =>
            measurement.InstrumentName == "nuplane.catalog.degraded.read"
            && measurement.Tags.TryGetValue("reason_code", out var reasonCode)
            && string.Equals(reasonCode, "loading-stale", StringComparison.Ordinal));
        Assert.Equal("loading", degradedRead.Tags["surface"]);
        Assert.Equal("loading-stale", degradedRead.Tags["reason_code"]);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenLoadingDiverges_EmitsDivergenceReasonInLogsAndMetrics()
    {
        var messages = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(messages)));
        using var collector = new MetricCollector("nuplane.catalog.loading.read", "nuplane.catalog.degraded.read");
        using var telemetry = new ReconciliationTelemetry();

        var loader = new PackageLoader();
        var package = new ResolvedPackage("pkg-failed", "1.0.0", "feed-a", "/path/does/not/exist", DateTimeOffset.UtcNow, "source-a");
        await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        var refreshTracker = new LoadingCatalogRefreshTracker();
        refreshTracker.MarkRefreshed("refresh-divergence");

        var catalog = new LoadingCatalog(
            new StubActivePackageCatalog(CreateSnapshot(package.Id, package.InstallPath, package.Version)),
            loader,
            new AssemblyScanCandidateProjector(loader),
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }),
            new ReconciliationLogger(loggerFactory.CreateLogger<ReconciliationLogger>()),
            new ReconciliationMetrics(telemetry));

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(LoadingCatalogAvailability.Available, snapshot.Availability);
        Assert.Equal(LoadingStatus.Failed, Assert.Single(snapshot.Packages).Status);
        Assert.Contains(messages, message =>
            message.Contains("Loading catalog read", StringComparison.Ordinal)
            && message.Contains("Availability=Available", StringComparison.Ordinal)
            && message.Contains("ReasonCode=loading-divergence", StringComparison.Ordinal));

        var loadingRead = Assert.Single(collector.Measurements, measurement =>
            measurement.InstrumentName == "nuplane.catalog.loading.read"
            && measurement.Tags.TryGetValue("reason_code", out var reasonCode)
            && string.Equals(reasonCode, "loading-divergence", StringComparison.Ordinal));
        Assert.Equal("Available", loadingRead.Tags["availability"]);
        Assert.Equal("1", loadingRead.Tags["package_count"]);
        Assert.Equal("loading-divergence", loadingRead.Tags["reason_code"]);

        var degradedRead = Assert.Single(collector.Measurements, measurement =>
            measurement.InstrumentName == "nuplane.catalog.degraded.read"
            && measurement.Tags.TryGetValue("reason_code", out var reasonCode)
            && string.Equals(reasonCode, "loading-divergence", StringComparison.Ordinal));
        Assert.Equal("loading", degradedRead.Tags["surface"]);
        Assert.Equal("loading-divergence", degradedRead.Tags["reason_code"]);
    }

    private static ActivePackageCatalogSnapshot CreateSnapshot(string packageId, string installPath, string version = "1.0.0") =>
        new(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [new ActivePackageDescriptor(packageId, version, "feed-a", "source-a", installPath, DateTimeOffset.UtcNow, "corr")],
            "read-obs");

    private sealed class StubActivePackageCatalog(ActivePackageCatalogSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector(params string[] instrumentNames)
        {
            var selected = instrumentNames.ToHashSet(StringComparer.Ordinal);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Nuplane.Runtime" && selected.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
                Measurements.Add(new MeasurementRecord(instrument.Name, measurement, ToDictionary(tags))));
            _listener.Start();
        }

        public List<MeasurementRecord> Measurements { get; } = [];

        public void Dispose() => _listener.Dispose();

        private static IReadOnlyDictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dictionary = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                dictionary[tag.Key] = tag.Value?.ToString();
            }

            return dictionary;
        }
    }

    private sealed record MeasurementRecord(string InstrumentName, long Value, IReadOnlyDictionary<string, string?> Tags);
}

