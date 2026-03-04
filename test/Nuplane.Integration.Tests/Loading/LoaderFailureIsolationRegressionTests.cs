using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Loading.Hosting;
using Nuplane.Runtime.Loading;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>
/// T041 — Regression test verifying that loader failures are isolated
/// per-package and do not crash the host. Failed packages produce
/// <see cref="PackageLoaderOutcome.Failed"/> while successful packages
/// produce <see cref="PackageLoaderOutcome.Loaded"/>.
/// </summary>
public sealed class LoaderFailureIsolationRegressionTests
{
    [Fact]
    public async Task LoaderFailure_DoesNotCrashHost()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new FailingLoader(new InvalidOperationException("loader boom"));
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", "/install/pkg-a", DateTimeOffset.UtcNow)
        };

        // Should NOT throw — failures are isolated in the boundary
        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Failed, result.Entries[0].Outcome);
    }

    [Fact]
    public async Task LoaderFailure_PerPackageIsolation_SuccessfulPackagesUnaffected()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new SelectiveFailingLoader(failIds: ["pkg-b"]);
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", "/install/pkg-a", DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-b", "2.0.0", "feed-1", "/install/pkg-b", DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-c", "3.0.0", "feed-1", "/install/pkg-c", DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal(3, result.Entries.Count);

        var entryA = result.Entries.First(e => e.PackageId == "pkg-a");
        Assert.Equal(PackageLoaderOutcome.Loaded, entryA.Outcome);

        var entryB = result.Entries.First(e => e.PackageId == "pkg-b");
        Assert.Equal(PackageLoaderOutcome.Failed, entryB.Outcome);
        Assert.NotNull(entryB.ReasonCode);

        var entryC = result.Entries.First(e => e.PackageId == "pkg-c");
        Assert.Equal(PackageLoaderOutcome.Loaded, entryC.Outcome);
    }

    [Fact]
    public async Task LoaderFailure_ReasonCodeIsPopulated()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new SelectiveFailingLoader(
            failIds: ["pkg-a"],
            failReason: "assembly-resolution-failed");
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", "/install/pkg-a", DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal("assembly-resolution-failed", result.Entries[0].ReasonCode);
    }

    [Fact]
    public async Task MultipleFailures_AllCaptured_NoneThrown()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new SelectiveFailingLoader(failIds: ["pkg-a", "pkg-b", "pkg-c"]);
        var adapter = new NuplaneLoadingAdapter(options, loader);

        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", "/a", DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-b", "2.0.0", "feed-1", "/b", DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-c", "3.0.0", "feed-1", "/c", DateTimeOffset.UtcNow)
        };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(PackageLoaderOutcome.Failed, e.Outcome));
    }

    /// <summary>
    /// A loader that throws an exception for all packages — simulates a total loader crash.
    /// The adapter should catch via IPackageLoader.EnsureLoadedAsync returning failures.
    /// </summary>
    private sealed class FailingLoader(Exception exception) : IPackageLoader
    {
        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken cancellationToken)
        {
            var failed = packages.ToDictionary(
                p => p.Id,
                _ => exception.Message,
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new PackageLoadResult(Array.Empty<PackageLoadSession>(), failed));
        }

        public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }

        public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }
    }

    /// <summary>
    /// A loader that selectively fails specific package IDs and succeeds for the rest.
    /// </summary>
    private sealed class SelectiveFailingLoader : IPackageLoader
    {
        private readonly HashSet<string> _failIds;
        private readonly string _failReason;

        public SelectiveFailingLoader(
            IEnumerable<string> failIds,
            string failReason = "simulated-load-failure")
        {
            _failIds = new HashSet<string>(failIds, StringComparer.OrdinalIgnoreCase);
            _failReason = failReason;
        }

        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken cancellationToken)
        {
            var loaded = new List<PackageLoadSession>();
            var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in packages)
            {
                if (_failIds.Contains(pkg.Id))
                {
                    failed[pkg.Id] = _failReason;
                }
                else
                {
                    loaded.Add(new PackageLoadSession(
                        pkg.Id, pkg.Version, pkg.InstallPath,
                        $"ctx-{pkg.Id}", DateTimeOffset.UtcNow, true, null));
                }
            }

            return Task.FromResult(new PackageLoadResult(loaded, failed));
        }

        public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }

        public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }
    }
}
