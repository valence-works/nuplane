using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using System.Runtime.Versioning;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogBoundaryTests
{
    [Fact]
    public async Task GetSnapshotAsync_ProjectsOnlyActivePackages_EvenWhenInactiveSessionsExist()
    {
        var activePackage = CreateResolvedPackage("pkg-active", "1.0.0");
        var inactivePackage = CreateResolvedPackage("pkg-inactive", "9.9.9");
        var loader = new PackageLoader();
        await loader.EnsureLoadedAsync([activePackage, inactivePackage], [], CancellationToken.None);

        var refreshTracker = new LoadingCatalogRefreshTracker();
        refreshTracker.MarkRefreshed("refresh-active-only");

        var catalog = CreateCatalog(
            new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor(activePackage.Id, activePackage.Version, activePackage.FeedName, activePackage.SourceName, activePackage.InstallPath, DateTimeOffset.UtcNow, "corr-active")],
                "read-active-only"),
            loader,
            refreshTracker);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = Assert.Single(snapshot.Packages);
        Assert.Equal("pkg-active", descriptor.PackageId);
        Assert.DoesNotContain(snapshot.Packages, package => package.PackageId == "pkg-inactive");
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsOnlyAssemblyLevelScanCandidatesUnderActiveInstallPath()
    {
        var package = CreateResolvedPackage("pkg-candidate", "1.0.0");
        var loader = new PackageLoader();
        await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        var refreshTracker = new LoadingCatalogRefreshTracker();
        refreshTracker.MarkRefreshed("refresh-candidates");

        var catalog = CreateCatalog(
            new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor(package.Id, package.Version, package.FeedName, package.SourceName, package.InstallPath, DateTimeOffset.UtcNow, "corr-candidate")],
                "read-candidates"),
            loader,
            refreshTracker);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = Assert.Single(snapshot.Packages);
        Assert.NotEmpty(descriptor.ScanCandidates);
        Assert.All(descriptor.ScanCandidates, candidate =>
        {
            Assert.StartsWith(package.InstallPath, candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(candidate.AssemblyFileName));
            Assert.True(
                candidate.CandidateKind is "PrimaryLoadAssembly" or "AdditionalManagedAssembly",
                $"Unexpected candidate kind '{candidate.CandidateKind}'.");
            Assert.DoesNotContain("plugin", candidate.SelectionReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("type", candidate.SelectionReason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void LoadingCatalogContracts_DoNotExposeDiscoveredTypeIdentities()
    {
        var loadingDescriptorProperties = typeof(LoadingPackageDescriptor)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var candidateProperties = typeof(AssemblyScanCandidate)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(loadingDescriptorProperties, name =>
            name.Contains("Type", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Plugin", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Module", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["AssemblyFileName", "AssemblyPath", "CandidateKind", "SelectionReason", "TargetFrameworkMoniker"],
            candidateProperties);
    }

    [Fact]
    public async Task GetSnapshotAsync_ForMultiTargetPackage_ProjectsOnlySelectedFrameworkCandidates()
    {
        var selectedFramework = GetHostFrameworkFolderName();
        var otherFramework = GetAlternateFrameworkFolderName(selectedFramework);
        var package = CreateResolvedPackage(
            "pkg-multi-target-catalog",
            "1.0.0",
            [
                (selectedFramework, ["pkg-multi-target-catalog", "host-helper"]),
                (otherFramework, ["pkg-multi-target-catalog", "other-helper"])
            ]);
        var loader = new PackageLoader();
        await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        var refreshTracker = new LoadingCatalogRefreshTracker();
        refreshTracker.MarkRefreshed("refresh-selected-framework-only");

        var catalog = CreateCatalog(
            new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor(package.Id, package.Version, package.FeedName, package.SourceName, package.InstallPath, DateTimeOffset.UtcNow, "corr-multi-target")],
                "read-selected-framework-only"),
            loader,
            refreshTracker);

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var descriptor = Assert.Single(snapshot.Packages);
        Assert.NotEmpty(descriptor.ScanCandidates);
        Assert.All(descriptor.ScanCandidates, candidate =>
        {
            Assert.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{selectedFramework}{Path.DirectorySeparatorChar}", candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{otherFramework}{Path.DirectorySeparatorChar}", candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(selectedFramework, candidate.TargetFrameworkMoniker);
        });
    }

    private static LoadingCatalog CreateCatalog(
        ActivePackageCatalogSnapshot snapshot,
        PackageLoader loader,
        LoadingCatalogRefreshTracker refreshTracker)
    {
        return new LoadingCatalog(
            new StubActivePackageCatalog(snapshot),
            loader,
            new AssemblyScanCandidateProjector(loader),
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }),
            new Nuplane.Observability.ReconciliationLogger(),
            new Nuplane.Observability.ReconciliationMetrics(new Nuplane.Observability.ReconciliationTelemetry()));
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-boundary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private static ResolvedPackage CreateResolvedPackage(
        string id,
        string version,
        IReadOnlyList<(string Framework, IReadOnlyList<string> AssemblyNames)> frameworks)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-boundary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var (framework, assemblyNames) in frameworks)
        {
            var frameworkDirectory = Directory.CreateDirectory(Path.Combine(root, "lib", framework));
            foreach (var assemblyName in assemblyNames)
            {
                File.Copy(typeof(PackageLoader).Assembly.Location, Path.Combine(frameworkDirectory.FullName, $"{assemblyName}.dll"), overwrite: true);
            }
        }

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private static string GetHostFrameworkFolderName()
    {
        var attribute = typeof(LoadingCatalogBoundaryTests).Assembly
            .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .OfType<TargetFrameworkAttribute>()
            .Single();

        var frameworkName = new FrameworkName(attribute.FrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETStandard" => $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETFramework" => $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}",
            _ => throw new InvalidOperationException($"Unsupported test host framework '{attribute.FrameworkName}'.")
        };
    }

    private static string GetAlternateFrameworkFolderName(string hostFrameworkFolder)
    {
        if (hostFrameworkFolder.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(hostFrameworkFolder, "netstandard2.0", StringComparison.OrdinalIgnoreCase)
                ? "netstandard1.0"
                : "netstandard2.0";
        }

        if (hostFrameworkFolder.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && hostFrameworkFolder.Contains('.', StringComparison.Ordinal))
        {
            var versionText = hostFrameworkFolder[3..];
            if (Version.TryParse(versionText, out var version))
            {
                var alternateMajor = version.Major > 1 ? version.Major - 1 : version.Major + 1;
                return $"net{alternateMajor}.{version.Minor}";
            }
        }

        return string.Equals(hostFrameworkFolder, "net472", StringComparison.OrdinalIgnoreCase)
            ? "net45"
            : "net472";
    }

    private sealed class StubActivePackageCatalog(ActivePackageCatalogSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.ToActivePackagesSnapshot());

        public Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

