using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class PartialFailureIsolationTests
{
    [Fact]
    public async Task ManualTrigger_WhenOnePackageFails_ContinuesApplyingUnaffectedPackages()
    {
        var source = new StaticSource(
        [
            new PackageRequest("pkg-good", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a"),
            new PackageRequest("pkg-bad", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        ]);

        var service = new ReconciliationService(
            new[] { source },
            new SourceTrustOptions
            {
                AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pkg-good", "pkg-bad" }
            },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new FailOneResolver("pkg-bad"),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions { MaxRetryAttempts = 0 });

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.Contains("pkg-bad", result.FailedPackages);
        Assert.Contains(result.ChangeSet.Added, p => string.Equals(p.Id, "pkg-good", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ChangeSet.Added, p => string.Equals(p.Id, "pkg-bad", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class FailOneResolver(string failingId) : INuGetPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.Id, failingId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("resolution failed");
            }

            return Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, request.FeedName ?? "default", $"/tmp/{request.Id}", DateTimeOffset.UtcNow, request.SourceName));
        }
    }
}
