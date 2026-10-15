using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantPostLoadingObserver_CanReacquireRootAfterLoadingWhileGraphRemainsProtected()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var observer = new PostLoadingObserverProbe();
        var setup = await CreatePostLoadingUpdateProviderAsync(fixture, observer);
        await using var provider = setup.Provider;
        observer.Callback = (changeSet, applied, cancellationToken) =>
            CapturePostLoadingObservationAsync(provider, fixture, observer, changeSet, applied, cancellationToken);

        try
        {
            var cycle = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("post-loading-reopen"), CancellationToken.None);

            Assert.False(cycle.IsDegraded);
            Assert.Equal(1, observer.CallbackCount);
            Assert.Null(observer.CallbackFailure);
            var observed = Assert.IsType<PostLoadingObservation>(observer.Observation);
            Assert.Equal("3.0.0", observed.ActiveVersion);
            Assert.Equal("3.0.0", observed.DescriptorVersion);
            Assert.Contains("Loaded", observed.RootSecondLoadStatuses);
            Assert.Contains("Root.Second", observed.RootSecondAssemblyNames);
            Assert.Single(observed.LiveGraphGenerations);
            Assert.Equal(observed.ActiveGraphGeneration, Assert.Single(observed.LiveGraphGenerations));
        }
        finally
        {
            await ReleasePostLoadingGraphAsync(provider, fixture);
            Directory.Delete(setup.FeedPath, recursive: true);
        }
    }

    [Fact]
    public async Task PostLoadingObserverWithoutDeferredLoader_StillRunsAfterPublishedStateAndAdmissionRelease()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var observer = new PostLoadingObserverProbe();
        var setup = await CreatePostLoadingUpdateProviderAsync(fixture, observer, withoutDeferredLoader: true);
        await using var provider = setup.Provider;
        observer.Callback = async (changeSet, applied, cancellationToken) =>
        {
            await using var operation = await provider.GetRequiredService<IPackageStoreAdmission>()
                .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, cancellationToken);
            using var borrow = operation.Owner!.Borrow();
            var state = await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(
                    provider.GetRequiredService<IStoreRegistry>())
                .ReadCoordinatedStateAsync(borrow, cancellationToken);
            observer.Observation = state.ActiveVersionById["Root.Second"];
        };

        try
        {
            Assert.Null(provider.GetService<ILeasedPackageGraphLoadingObserver>());
            var cycle = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("post-loading-only"), CancellationToken.None);

            Assert.False(cycle.IsDegraded);
            Assert.Equal(1, observer.CallbackCount);
            Assert.Null(observer.CallbackFailure);
            Assert.Equal("3.0.0", Assert.IsType<string>(observer.Observation));
        }
        finally
        {
            await ReleasePostLoadingGraphAsync(provider, fixture);
            Directory.Delete(setup.FeedPath, recursive: true);
        }
    }

    [Fact]
    public async Task PostLoadingObserverWithoutDeferredLoader_GenerateModeWritesLockAfterCycleCompletion()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var observer = new PostLoadingObserverProbe();
        var setup = await CreatePostLoadingUpdateProviderAsync(
            fixture, observer, withoutDeferredLoader: true, generateLockFile: true);
        await using var provider = setup.Provider;
        var lockPath = Path.Combine(Path.GetDirectoryName(fixture.StatePaths["member-b"])!, "nuplane.lock.json");
        var lockStore = new LockFileStore(Options.Create(new LockFileOptions { Path = lockPath }));
        await lockStore.WriteAsync(new PackageLockFile("1.0", DateTimeOffset.UtcNow, []), CancellationToken.None);
        var legacyBytes = await File.ReadAllBytesAsync(lockPath);

        try
        {
            var cycle = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("post-loading-generate-lock"), CancellationToken.None);

            Assert.False(cycle.IsDegraded);
            Assert.Equal(1, observer.CallbackCount);
            Assert.Null(observer.CallbackFailure);
            var generated = await lockStore.ReadAsync(CancellationToken.None);
            Assert.NotNull(generated);
            Assert.Equal("2.0", generated.SchemaVersion);
            Assert.Contains(generated.Packages, static entry =>
                string.Equals(entry.Id, "Root.Second", StringComparison.OrdinalIgnoreCase) && entry.Version == "3.0.0");
            Assert.NotEqual(legacyBytes, await File.ReadAllBytesAsync(lockPath));
        }
        finally
        {
            await ReleasePostLoadingGraphAsync(provider, fixture);
            Directory.Delete(setup.FeedPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostLoadingObserverCancellationOrAdmissionRefusal_ReleasesRootAndGraphOwnership(bool cancel)
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var observer = new PostLoadingObserverProbe();
        var setup = await CreatePostLoadingUpdateProviderAsync(fixture, observer);
        await using var provider = setup.Provider;
        observer.Callback = async (changeSet, applied, cancellationToken) =>
        {
            await CapturePostLoadingObservationAsync(
                provider, fixture, observer, changeSet, applied, cancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw observer.ExpectedRefusal;
        };

        try
        {
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                        ReconciliationTrigger.Manual("post-loading-cancel"), cancellation.Token));
                Assert.True(cancellation.IsCancellationRequested);
                Assert.IsAssignableFrom<OperationCanceledException>(observer.CallbackFailure);
            }
            else
            {
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                    provider.GetRequiredService<IReconciliationService>().TriggerAsync(
                        ReconciliationTrigger.Manual("post-loading-refusal"), CancellationToken.None));
                Assert.Same(observer.ExpectedRefusal, refusal);
                Assert.Same(refusal, observer.CallbackFailure);
            }

            Assert.Equal(1, observer.CallbackCount);
            var observed = Assert.IsType<PostLoadingObservation>(observer.Observation);
            Assert.Equal("3.0.0", observed.ActiveVersion);
            Assert.Single(observed.LiveGraphGenerations);
            Assert.Equal(observed.ActiveGraphGeneration, Assert.Single(observed.LiveGraphGenerations));
        }
        finally
        {
            await ReleasePostLoadingGraphAsync(provider, fixture);
            Directory.Delete(setup.FeedPath, recursive: true);
        }
    }

    private static async Task<(ServiceProvider Provider, string FeedPath)> CreatePostLoadingUpdateProviderAsync(
        GraphUseFixture fixture,
        PostLoadingObserverProbe observer,
        bool withoutDeferredLoader = false,
        bool generateLockFile = false)
    {
        var feedPath = Path.Combine(Path.GetTempPath(), "nuplane-post-loading-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(feedPath);
        var packageBytes = generateLockFile
            ? CreatePackageArchive("Root.Second", "3.0.0")
            : CreatePackageArchive("Root.Second", "3.0.0", "Shared.Dependency", "2.1.0");
        await File.WriteAllBytesAsync(Path.Combine(feedPath, "root.second.3.0.0.nupkg"), packageBytes);
        var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(packageBytes));
        var request = new PackageRequest("ROOT.SECOND", "[2.0.0,4.0.0)", "feed",
            PackageUpdatePolicy.Range, "post-loading-test");
        var localFeed = new FeedDefinition("local-cache", new Uri(feedPath + Path.DirectorySeparatorChar));
        var remoteFeed = new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json"));
        var provider = fixture.CreateProvider("member-b", desiredRequests: [request], remoteFeed: remoteFeed,
            additionalFeeds: [localFeed], participant: true, observer: observer,
            configureServices: services =>
            {
                if (withoutDeferredLoader)
                    services.RemoveAll<ILeasedPackageGraphLoadingObserver>();
                if (generateLockFile)
                    services.Configure<LockFileOptions>(options => options.Mode = LockFileMode.Generate);
            });
        var lockPath = Path.Combine(Path.GetDirectoryName(fixture.StatePaths["member-b"])!, "nuplane.lock.json");
        var lockStore = new LockFileStore(Options.Create(new LockFileOptions { Path = lockPath }));
        await lockStore.WriteAsync(new PackageLockFile("2.0", DateTimeOffset.UtcNow,
            [new PackageLockEntry("Root.Second", "3.0.0", "local-cache", expectedHash, DateTimeOffset.UtcNow)]),
            CancellationToken.None);
        return (provider, feedPath);
    }

    private static async Task CapturePostLoadingObservationAsync(
        IServiceProvider provider,
        GraphUseFixture fixture,
        PostLoadingObserverProbe observer,
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken)
    {
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();
        StoreStateRecord state;
        await using (var operation = await admission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Maintenance, cancellationToken))
        {
            using var borrow = operation.Owner!.Borrow();
            state = await Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>())
                .ReadCoordinatedStateAsync(borrow, cancellationToken);
        }

        var loadState = await provider.GetRequiredService<IPackageLoadStateCatalog>().GetLoadStateAsync(cancellationToken);
        var assemblyEntries = await provider.GetRequiredService<IPackageAssemblyCatalog>()
            .GetPackagedAssembliesAsync(cancellationToken);
        var graphUses = await InspectGraphUseRecordsAsync(admission, fixture);
        var activeGraph = Assert.Single(state.ActiveGraphsByIdNormalized.Values,
            graph => graph.Status == GraphActivationStatus.Active);
        var rootLoadStatuses = loadState.Packages
            .Where(static package => string.Equals(package.PackageId, "Root.Second", StringComparison.OrdinalIgnoreCase))
            .Select(static package => package.Status.ToString()).ToArray();
        var rootAssemblyNames = assemblyEntries
            .Where(static package => string.Equals(package.PackageId, "Root.Second", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static package => package.Assemblies)
            .Select(static assembly => assembly.GetName().Name ?? string.Empty).ToArray();
        var liveGraphGenerations = graphUses.Entries
            .Where(static entry => entry.OwnershipState == GraphUseRecordOwnershipState.Live)
            .Select(static entry => entry.Record.GraphSnapshot.GenerationId).ToArray();

        observer.Observation = new PostLoadingObservation(
            state.ActiveVersionById.GetValueOrDefault("Root.Second"),
            state.ActivePackageDescriptorsByIdNormalized.GetValueOrDefault("Root.Second")?.Version,
            activeGraph.GenerationId,
            rootLoadStatuses,
            rootAssemblyNames,
            liveGraphGenerations);
    }

    private static async Task ReleasePostLoadingGraphAsync(ServiceProvider provider, GraphUseFixture fixture)
    {
        provider.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        await fixture.WaitForGraphUseReleaseAsync(provider.GetRequiredService<IPackageStoreAdmission>());
        await using var reopened = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, reopened.Status);
    }

    private sealed class PostLoadingObserverProbe : IPostLoadingNuplaneObserver
    {
        private int _callbackCount;

        internal Func<PackageChangeSet, IReadOnlyList<ResolvedPackage>, CancellationToken, Task>? Callback { get; set; }
        internal int CallbackCount => Volatile.Read(ref _callbackCount);
        internal Exception? CallbackFailure { get; private set; }
        internal object? Observation { get; set; }
        internal PackageStoreAdmissionException ExpectedRefusal { get; } = new(
            PackageStoreAdmissionReason.UnknownAuthority, "Post-loading admission was refused by the observer.");

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct) => Task.CompletedTask;
        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnPackageFailedAsync(string packageId, Exception exception, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            PackageStoreOperationBorrow borrow, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task OnPackageLoadingCompletedAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            try
            {
                if (Callback is not null)
                    await Callback(changeSet, appliedPackages, cancellationToken);
            }
            catch (Exception exception)
            {
                CallbackFailure = exception;
                throw;
            }
        }
    }

    private sealed record PostLoadingObservation(
        string? ActiveVersion,
        string? DescriptorVersion,
        string ActiveGraphGeneration,
        string[] RootSecondLoadStatuses,
        string[] RootSecondAssemblyNames,
        string[] LiveGraphGenerations);
}
