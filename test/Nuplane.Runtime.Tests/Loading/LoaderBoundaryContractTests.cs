using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Loading.Hosting;
using Nuplane.Runtime.Loading;

namespace Nuplane.Runtime.Tests.Loading;

/// <summary>
/// T039 — Contract test verifying the loader boundary outcomes
/// (Loaded, Failed, Skipped) from the <see cref="NuplaneLoadingAdapter"/>.
/// </summary>
public sealed class LoaderBoundaryContractTests
{
    [Fact]
    public async Task Disabled_AllPackagesSkipped()
    {
        var options = new LoadingOptions { Enabled = false };
        var adapter = new NuplaneLoadingAdapter(options, new FakeLoader());
        var packages = new[] { Pkg("pkg-a", "1.0.0") };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Skipped, result.Entries[0].Outcome);
        Assert.Equal("loader-disabled", result.Entries[0].ReasonCode);
    }

    [Fact]
    public async Task Enabled_SuccessfulLoad_ReturnsLoadedOutcome()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new FakeLoader(successIds: ["pkg-a"]);
        var adapter = new NuplaneLoadingAdapter(options, loader);
        var packages = new[] { Pkg("pkg-a", "1.0.0") };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Loaded, result.Entries[0].Outcome);
        Assert.Null(result.Entries[0].ReasonCode);
    }

    [Fact]
    public async Task Enabled_FailedLoad_ReturnsFailedOutcomeWithReason()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new FakeLoader(failedIds: new Dictionary<string, string>
        {
            ["pkg-a"] = "assembly-not-found"
        });
        var adapter = new NuplaneLoadingAdapter(options, loader);
        var packages = new[] { Pkg("pkg-a", "1.0.0") };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal(PackageLoaderOutcome.Failed, result.Entries[0].Outcome);
        Assert.Equal("assembly-not-found", result.Entries[0].ReasonCode);
    }

    [Fact]
    public async Task Enabled_MixedOutcomes_PerPackageIsolation()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new FakeLoader(
            successIds: ["pkg-a"],
            failedIds: new Dictionary<string, string> { ["pkg-b"] = "load-error" });
        var adapter = new NuplaneLoadingAdapter(options, loader);
        var packages = new[] { Pkg("pkg-a", "1.0.0"), Pkg("pkg-b", "2.0.0") };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal(2, result.Entries.Count);

        var entryA = result.Entries.First(e => e.PackageId == "pkg-a");
        Assert.Equal(PackageLoaderOutcome.Loaded, entryA.Outcome);

        var entryB = result.Entries.First(e => e.PackageId == "pkg-b");
        Assert.Equal(PackageLoaderOutcome.Failed, entryB.Outcome);
        Assert.Equal("load-error", entryB.ReasonCode);
    }

    [Fact]
    public async Task Enabled_EmptyPackages_ReturnsSkipped()
    {
        var options = new LoadingOptions { Enabled = true };
        var adapter = new NuplaneLoadingAdapter(options, new FakeLoader());

        var result = await adapter.LoadAsync([], "corr-1", CancellationToken.None);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task PreservesPackageVersionInEntries()
    {
        var options = new LoadingOptions { Enabled = true };
        var loader = new FakeLoader(successIds: ["pkg-a"]);
        var adapter = new NuplaneLoadingAdapter(options, loader);
        var packages = new[] { Pkg("pkg-a", "4.2.1") };

        var result = await adapter.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal("4.2.1", result.Entries[0].Version);
    }

    [Fact]
    public void NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NuplaneLoadingAdapter(null!, new FakeLoader()));
    }

    [Fact]
    public void NullLoader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NuplaneLoadingAdapter(new LoadingOptions(), null!));
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-1", $"/install/{id}", DateTimeOffset.UtcNow);

    private sealed class FakeLoader : IPackageLoader
    {
        private readonly HashSet<string> _successIds;
        private readonly Dictionary<string, string> _failedIds;

        public FakeLoader(
            IEnumerable<string>? successIds = null,
            Dictionary<string, string>? failedIds = null)
        {
            _successIds = successIds is not null
                ? new HashSet<string>(successIds, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _failedIds = failedIds ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (_failedIds.TryGetValue(pkg.Id, out var reason))
                {
                    failed[pkg.Id] = reason;
                }
                else if (_successIds.Contains(pkg.Id))
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
