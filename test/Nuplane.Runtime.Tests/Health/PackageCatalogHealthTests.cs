using Nuplane.Abstractions;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Health;

public sealed class PackageCatalogHealthTests
{
    [Fact]
    public async Task MissingDescriptorData_ProducesPackageCatalogDegradedReason()
    {
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase));

        var evaluator = new ReconciliationHealthEvaluator();
        var catalog = new ActivePackageCatalog(
            new StubStoreRegistry(state),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
        var projector = new OperationalSnapshotProjector(
            evaluator,
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()),
            [new PackageCatalogOperationalStateContributor(new StubStoreRegistry(state))]);

        await catalog.GetSnapshotAsync(CancellationToken.None);
        var snapshot = await projector.ProjectAsync("health-1", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Contains("package-catalog-issues:1", snapshot.DegradedReasons);
    }

    private sealed class StubStoreRegistry(StoreStateRecord state) : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(state.ActiveVersionById);

        public Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(state);

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> activeVersions, IReadOnlyDictionary<string, string> successfullyApplied, string correlationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string packageId, string stage, string message, string correlationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string sourceName, SourceSnapshotRef snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

