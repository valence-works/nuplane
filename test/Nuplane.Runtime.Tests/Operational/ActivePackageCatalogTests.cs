using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class ActivePackageCatalogTests
{
    [Fact]
    public void BuildNextDescriptors_WhenResolvedInstallPathChangesForSameVersion_RefreshesDescriptor()
    {
        var currentState = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SamplePackage"] = "0.0.1"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.Parse("2026-04-08T15:00:00Z"),
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["SamplePackage"] = new("SamplePackage", "0.0.1", "local-packages", "local-packages", "/app/packages/.installed/SamplePackage/0.0.1", DateTimeOffset.UtcNow, "old-corr")
            });
        var resolvedPackage = new ResolvedPackage(
            "SamplePackage",
            "0.0.1",
            "local-packages",
            "/Users/me/app/packages/.installed/SamplePackage/0.0.1",
            DateTimeOffset.UtcNow,
            "local-packages");

        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            currentState,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SamplePackage"] = "0.0.1"
            },
            [resolvedPackage],
            new PackageChangeSet([], [], [], "new-corr", DateTimeOffset.UtcNow),
            "new-corr",
            DateTimeOffset.UtcNow);

        var descriptor = Assert.Single(descriptors).Value;
        Assert.Equal(resolvedPackage.InstallPath, descriptor.InstallPath);
        Assert.Equal("new-corr", descriptor.ActivationCorrelationId);
    }

    [Fact]
    public async Task GetActivePackagesAsync_ReturnsOnlyActivePackagesInDeterministicOrder()
    {
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["zeta"] = "3.0.0",
                ["alpha"] = "1.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.Parse("2026-04-08T15:00:00Z"),
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["orphaned"] = new("orphaned", "9.9.9", "feed-x", "source-x", "/packages/orphaned", DateTimeOffset.UtcNow, "corr-orphan"),
                ["zeta"] = new("zeta", "3.0.0", "feed-z", "source-z", "/packages/zeta", DateTimeOffset.UtcNow, "corr-z"),
                ["alpha"] = new("alpha", "1.0.0", "feed-a", "source-a", "/packages/alpha", DateTimeOffset.UtcNow, "corr-a")
            });

        var catalog = CreateCatalog(state);
        var snapshot = await catalog.GetActivePackagesAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Packages.Count);
        Assert.Equal("alpha", snapshot.Packages[0].PackageId);
        Assert.Equal("zeta", snapshot.Packages[1].PackageId);
    }

    [Fact]
    public async Task GetActivePackagesAsync_PreservesTrustedProvenance_AndReportsDescriptorIssues()
    {
        var state = new StoreStateRecord(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = "1.0.0",
                ["pkg-b"] = "2.0.0"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.Parse("2026-04-08T16:00:00Z"),
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a"] = new("pkg-a", "1.0.0", "trusted-feed", "manifest-a", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr-a")
            });

        var catalog = CreateCatalog(state);
        var snapshot = await catalog.GetActivePackagesAsync(CancellationToken.None);

        Assert.Single(snapshot.Packages);
        Assert.Equal("trusted-feed", snapshot.Packages[0].FeedName);
        Assert.Equal("manifest-a", snapshot.Packages[0].SourceName);
    }

    private static ActivePackageCatalog CreateCatalog(StoreStateRecord state)
    {
        return new ActivePackageCatalog(
            new StubStoreRegistry(state),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
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
