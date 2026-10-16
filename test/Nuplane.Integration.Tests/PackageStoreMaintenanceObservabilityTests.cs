using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Admin;
using Nuplane.Operational;
using Nuplane.Store.Coordination;
using Nuplane.Store.Maintenance;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicMaintenance_RecordsCorrelatedOutcomesAndOnlyObservedPhysicalDeletes()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var logs = new MaintenanceLogProvider();
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var listener = CreateMaintenanceMeterListener(measurements);
        await using var provider = CreateMaintenanceProvider(fixture, services =>
            services.AddLogging(builder => builder.AddProvider(logs)));
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        const string correlationId = "maintenance-observability-test";
        var retention = new PackageStoreRetentionPolicy(0);

        var preview = await operations.PruneAsync(new PackageStorePruneRequest(
            Retention: retention, CorrelationId: correlationId));
        var executed = await operations.PruneAsync(Confirm(preview, retention) with { CorrelationId = correlationId });

        Assert.Equal("Preview", preview.Status);
        Assert.Equal("Completed", executed.Status);
        var actualDeletes = executed.Candidates.Count(static candidate => candidate.PhysicalOutcome == "Deleted");
        Assert.True(actualDeletes > 0);
        Assert.Contains(measurements, static measurement =>
            measurement.Instrument == "nuplane.package_store.maintenance.operations" &&
            measurement.Tags.GetValueOrDefault("outcome") as string == "preview");
        Assert.Contains(measurements, static measurement =>
            measurement.Instrument == "nuplane.package_store.maintenance.operations" &&
            measurement.Tags.GetValueOrDefault("outcome") as string == "completed");
        Assert.True(measurements.Where(static measurement =>
                measurement.Instrument == "nuplane.package_store.maintenance.physical" &&
                measurement.Tags.GetValueOrDefault("outcome") as string == "deleted")
            .Sum(static measurement => measurement.Value) == actualDeletes);
        Assert.DoesNotContain(measurements.Where(static measurement =>
                measurement.Instrument == "nuplane.package_store.maintenance.physical"),
            static measurement => measurement.Tags.Values.Contains("eligible", StringComparer.OrdinalIgnoreCase));
        Assert.All(measurements, static measurement => Assert.DoesNotContain(measurement.Tags.Keys,
            key => key is "correlation.id" or "root_label" or "path" or "member_id"));

        var operationLogs = logs.Entries.Where(static entry => entry.EventId == 2000).ToArray();
        Assert.Equal(2, operationLogs.Length);
        Assert.All(operationLogs, entry => Assert.Equal(correlationId, entry.Fields["CorrelationId"]));
        var executeLog = Assert.Single(operationLogs, static entry => Equals(entry.Fields["Status"], "Completed"));
        Assert.Equal(actualDeletes, executeLog.Fields["PhysicalDeleted"]);
        Assert.Equal(executed.Uses.Count, (int)executeLog.Fields["GraphUseCount"]!);
        var retainedReason = executed.Candidates.Where(static candidate => candidate.Classification == "Retained")
            .SelectMany(static candidate => candidate.Reasons).First();
        Assert.Contains(retainedReason, (string)executeLog.Fields["ProtectionReasons"]!);
    }

    [Theory]
    [InlineData(false, "Failed", "failed")]
    [InlineData(true, "Cancelled", "cancelled")]
    public async Task PublicMaintenance_PartialFailureOrCancellationIsMeasuredAndHealthClearsAfterResume(
        bool cancelAtCheckpoint, string expectedStatus, string expectedOutcome)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelAfterRemoval = 0;
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var listener = CreateMaintenanceMeterListener(measurements);
        await using var provider = CreateMaintenanceProvider(fixture, services =>
        {
            services.AddNuplanePackageStoreMaintenance();
            services.AddNuplanePackageStoreMaintenance();
            services.AddSingleton<PackageStoreExecutionService>(sp => new(
                ((PackageStoreAdmission)sp.GetRequiredService<IPackageStoreAdmission>()).Registry,
                sp.GetRequiredService<PackageStoreInspectionService>(),
                deletionExecutionCheckpoint: (point, _) =>
                {
                    if (point == PackageStoreDeletionExecutionPoint.AfterPayloadEntryRemoved &&
                        Interlocked.Exchange(ref cancelAfterRemoval, 1) == 0)
                    {
                        if (cancelAtCheckpoint)
                            cancellation.Cancel();
                        else
                            throw new IOException("Injected deletion checkpoint failure.");
                    }
                }));
        });
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        _ = await GetMaintenanceContributionAsync(provider);
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest());
        var interrupted = await operations.PruneAsync(Confirm(preview, retention),
            cancelAtCheckpoint ? cancellation.Token : CancellationToken.None);

        Assert.Equal(expectedStatus, interrupted.Status);
        Assert.Contains(interrupted.Candidates, static candidate => candidate.PhysicalOutcome == "PartiallyRemoved");
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "nuplane.package_store.maintenance.operations" &&
            measurement.Tags.GetValueOrDefault("outcome") == expectedOutcome);
        Assert.Contains(measurements, static measurement =>
            measurement.Instrument == "nuplane.package_store.maintenance.physical" &&
            measurement.Tags.GetValueOrDefault("outcome") == "partially_removed" && measurement.Value > 0);
        Assert.Equal(new[] { $"package-store-maintenance:{expectedOutcome}" },
            (await GetMaintenanceContributionAsync(provider)).DegradedReasons);

        var repeatedPreview = await operations.PruneAsync(new PackageStorePruneRequest());
        Assert.Equal("Preview", repeatedPreview.Status);
        Assert.Equal(new[] { $"package-store-maintenance:{expectedOutcome}" },
            (await GetMaintenanceContributionAsync(provider)).DegradedReasons);

        var completed = await operations.PruneAsync(Confirm(repeatedPreview, retention));

        Assert.Equal("Completed", completed.Status);
        Assert.Empty((await GetMaintenanceContributionAsync(provider)).DegradedReasons);
    }

    [Fact]
    public async Task PublicMaintenance_ThrowingLoggerCannotMaskCompletedPhysicalFacts()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture, services =>
            services.AddLogging(builder => builder.AddProvider(new ThrowingMaintenanceLogProvider())));
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));

        var execution = await operations.PruneAsync(Confirm(preview, retention));

        Assert.Equal("Completed", execution.Status);
        Assert.Contains(execution.Candidates, static candidate => candidate.PhysicalOutcome == "Deleted");
    }

    private static async Task<OperationalStateContribution> GetMaintenanceContributionAsync(IServiceProvider provider)
    {
        var contributor = Assert.Single(provider.GetServices<IOperationalStateContributor>(),
            static candidate => candidate.GetType().FullName ==
                "Nuplane.Admin.PackageStoreMaintenanceOperationalState");
        var contribution = await contributor.ContributeAsync(CancellationToken.None);
        Assert.Equal("package-store-maintenance", contribution.Contributor);
        return contribution;
    }

    [Fact]
    public async Task PublicMaintenance_RefusalPersistsThroughPreviewUntilSuccessfulExecution()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture, services =>
        {
            services.AddNuplanePackageStoreMaintenance();
            services.AddNuplanePackageStoreMaintenance();
        });
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PruneAsync(new PackageStorePruneRequest());
        var wrongEpoch = await operations.PruneAsync(new PackageStorePruneRequest(
            preview.RootLabel, null, PackageStorePruneMode.Execute,
            new(preview.RootLabel, preview.RootIdentity!, preview.EnrollmentEpoch!.Value + 1, null)));

        Assert.Equal("Refused", wrongEpoch.Status);
        Assert.Equal(new[] { "package-store-maintenance:refused" },
            (await GetMaintenanceContributionAsync(provider)).DegradedReasons);

        var repeatedPreview = await operations.PruneAsync(new PackageStorePruneRequest());
        Assert.Equal("Preview", repeatedPreview.Status);
        Assert.Equal(new[] { "package-store-maintenance:refused" },
            (await GetMaintenanceContributionAsync(provider)).DegradedReasons);

        var completed = await operations.PruneAsync(Confirm(repeatedPreview, null));

        Assert.Equal("Completed", completed.Status);
        Assert.Empty((await GetMaintenanceContributionAsync(provider)).DegradedReasons);
    }

    private static MeterListener CreateMaintenanceMeterListener(ConcurrentBag<MetricMeasurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "Nuplane.Admin")
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
                values[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            measurements.Add(new(instrument.Name, value, values));
        });
        listener.Start();
        return listener;
    }

    private sealed record MetricMeasurement(string Instrument, long Value, IReadOnlyDictionary<string, string> Tags);

    private sealed class ThrowingMaintenanceLogProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingMaintenanceLogger(categoryName);

        public void Dispose() { }

        private sealed class ThrowingMaintenanceLogger(string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            private bool IsMaintenanceCategory => categoryName == "Nuplane.Admin.PackageStoreMaintenanceService";

            public bool IsEnabled(LogLevel logLevel) => IsMaintenanceCategory;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsMaintenanceCategory)
                    throw new InvalidOperationException("Injected maintenance logger callback failure.");
            }
        }
    }

    private sealed class MaintenanceLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<MaintenanceLogEntry> _entries = new();
        internal IReadOnlyList<MaintenanceLogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new MaintenanceLogger(_entries, categoryName);

        public void Dispose() { }

        private sealed class MaintenanceLogger(
            ConcurrentQueue<MaintenanceLogEntry> entries,
            string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                entries.Enqueue(new(categoryName, logLevel, eventId.Id, fields, formatter(state, exception)));
            }
        }
    }

    private sealed record MaintenanceLogEntry(
        string Category,
        LogLevel Level,
        int EventId,
        IReadOnlyDictionary<string, object?> Fields,
        string Message);
}
