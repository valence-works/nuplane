using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeferredLoading_WithReplacedFailureContributor_ReturnsFailuresAndDrainsModuleCorrelation(
        bool corruptDependency, bool cancelFailureObserver)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var replacement = new ReplacementFailureContributor();
        await using var provider = CreateProvider(fixture, "member-a", services =>
        {
            services.AddSingleton<ICycleFailureContributor>(replacement);
            if (cancelFailureObserver)
                services.AddSingleton<IPackageLoadingObserver>(new CancellingFailureObserver(cancellation));
        });
        Assert.Same(replacement, provider.GetRequiredService<ICycleFailureContributor>());
        var loaderObserver = provider.GetRequiredService<ILeasedPackageGraphLoadingObserver>();
        Assert.Same(loaderObserver, Assert.Single(provider.GetServices<INuplaneObserver>().OfType<PackageAutoLoadingObserver>()));
        var tracker = provider.GetRequiredService<LoadingFailureTracker>();
        tracker.RecordFailure("other-cycle", "Other.Package", "Preserved diagnostic");
        var graph = fixture.Graphs["member-a"];
        PackageGraphUseLeaseOwner? owner = null;
        try
        {
            owner = await AcquireGraphUseAsync(provider, graph);
            if (corruptDependency)
            {
                var dependency = graph.Packages.Single(package => package.Id == "Shared.Dependency");
                await File.WriteAllBytesAsync(Path.Combine(dependency.InstallPath, "Shared.Dependency.dll"), [0, 1, 2, 3]);
            }

            var changeSet = new PackageChangeSet(graph.Packages, [], [], "deferred-correlation", DateTimeOffset.UtcNow);
            var selection = new ResolvedPackageGraphSelection(graph.Graph, graph.Requests, graph.Packages);
            var handoff = new LeasedPackageGraphLoadingHandoff(changeSet, graph.Requests,
                [new LeasedPackageGraphSelection(selection, owner)]);
            LeasedPackageGraphLoadingResult? result = null;
            if (cancelFailureObserver)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    loaderObserver.LoadAsync(handoff, currentActiveVersions: null, cancellation.Token));
                Assert.True(cancellation.IsCancellationRequested);
            }
            else
            {
                result = await loaderObserver.LoadAsync(handoff, currentActiveVersions: null, cancellation.Token);
            }

            Assert.Empty(tracker.TakeFailedPackageIds(changeSet.CorrelationId));
            Assert.Equal(["Other.Package"], tracker.TakeFailedPackageIds("other-cycle"));
            if (corruptDependency)
            {
                if (result is not null)
                    Assert.Equal(["Root.First", "Shared.Dependency"], result.Failures.Select(failure => failure.PackageId)
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray());
                Assert.True(tracker.TryGetFailureDiagnostic("Root.First", out var diagnostic)
                    || tracker.TryGetFailureDiagnostic("Shared.Dependency", out diagnostic));
                Assert.False(string.IsNullOrWhiteSpace(diagnostic));
            }
            else
            {
                Assert.Empty(Assert.IsType<LeasedPackageGraphLoadingResult>(result).Failures);
                AssertGraphLoaded(provider, graph, "Root.First", "Shared.Dependency");
            }
        }
        finally
        {
            await UnloadAndWaitForReleaseAsync(provider, fixture, owner);
        }
    }

    private sealed class ReplacementFailureContributor : IPackagePathIndependentCycleFailureContributor
    {
        public IReadOnlyList<string> TakeFailedPackageIds(string correlationId) => [];
    }

    private sealed class CancellingFailureObserver(CancellationTokenSource cancellation) : IPackageLoadingObserver
    {
        public Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
