using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using System.Reflection;
using System.Reflection.Emit;
using static Nuplane.Runtime.Tests.TestSupport.HostProvidedPackagesTestSupport;

namespace Nuplane.Runtime.Tests.Feeds;

public sealed class PackageDependencyGraphResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-graph-resolver-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResolveAsync_RootOnlyDesiredInput_ResolvesRootAndDependencyGraph()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Equal(["Plugin.Dependency", "Plugin.Root"], result.ResolvedPackages.Select(static package => package.Id).Order(StringComparer.OrdinalIgnoreCase));
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Plugin.Root"], graph.Roots.Select(static node => node.PackageId));
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Dependency" && node.Role == PackageNodeRole.Dependency);
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Plugin.Root", edge.FromPackageId);
        Assert.Equal("Plugin.Dependency", edge.ToPackageId);
        Assert.Equal("[1.0.0]", edge.RequestedVersionRange);
    }

    [Fact]
    public async Task ResolveAsync_DependencyRequest_DoesNotPinRootFeed()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var dependencyRequest = Assert.Single(resolver.Requests);
        Assert.Equal("Plugin.Dependency", dependencyRequest.Id);
        Assert.Null(dependencyRequest.FeedName);
        Assert.Equal("dependency-of:Plugin.Root", dependencyRequest.SourceName);
    }

    [Fact]
    public async Task ResolveAsync_BareDependencyVersion_TreatsVersionAsInclusiveMinimum()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "8.0.2");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "10.0.3");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = [dependency]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Dependency" && node.Version == "10.0.3");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("[8.0.2,)", edge.RequestedVersionRange);
        var request = Assert.Single(resolver.Requests);
        Assert.Equal("[8.0.2,)", request.VersionRange);
    }

    [Fact]
    public async Task ResolveAsync_WhenHigherDirectDependencySatisfiesTransitiveBaseline_ReusesSelectedDependency()
    {
        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependenciesXml: """
                <dependencies>
                  <dependency id="Plugin.Direct" version="10.0.3" />
                  <dependency id="Plugin.Transitive" version="[1.0.0]" />
                </dependencies>
                """);
        var direct = CreateInstalledPackage("Plugin.Direct", "10.0.3");
        var transitive = CreateInstalledPackage("Plugin.Transitive", "1.0.0", dependencyId: "Plugin.Direct", dependencyVersionRange: "8.0.2");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Direct"] = [direct],
                ["Plugin.Transitive"] = [transitive]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes, static node => node.PackageId == "Plugin.Direct");
        Assert.Equal(2, graph.Edges.Count(static edge => edge.ToPackageId == "Plugin.Direct"));
        Assert.Single(resolver.Requests, static request => request.Id == "Plugin.Direct");
    }

    [Fact]
    public async Task ResolveAsync_MultipleRoots_UnifiesSharedDependencyWithNuGetLowestApplicableVersion()
    {
        var leftRoot = CreateInstalledPackage("Plugin.Left", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "1.0.0");
        var rightRoot = CreateInstalledPackage("Plugin.Right", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "2.0.0");
        var resolver = new VersionRangePackageResolver(
            new Dictionary<string, IReadOnlyList<ResolvedPackage>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Shared"] =
                [
                    CreateInstalledPackage("Plugin.Shared", "1.0.0"),
                    CreateInstalledPackage("Plugin.Shared", "2.0.0"),
                    CreateInstalledPackage("Plugin.Shared", "3.0.0")
                ]
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [
                new PackageRequest("Plugin.Left", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source"),
                new PackageRequest("Plugin.Right", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")
            ],
            (request, _) => Task.FromResult(request.Id == "Plugin.Left" ? leftRoot : rightRoot),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Plugin.Left", "Plugin.Right"], graph.Roots.Select(static node => node.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Single(graph.Nodes, static node => node.PackageId == "Plugin.Shared");
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Shared" && node.Version == "2.0.0");
        Assert.DoesNotContain(graph.Nodes, static node => node.PackageId == "Plugin.Shared" && node.Version == "3.0.0");
    }

    [Fact]
    public async Task ResolveAsync_WhenCancelledBeforeNuGetSolve_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(root);
            },
            cts.Token));
    }

    [Fact]
    public async Task ResolveAsync_WithNonNormalizedPackageVersion_MapsNuGetSelectedIdentityBackToResolvedPackage()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var package = Assert.Single(result.ResolvedPackages);
        Assert.Equal("Plugin.Root", package.Id);
        Assert.Equal("1.0", package.Version);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Root" && node.Version == "1.0");
    }

    [Fact]
    public async Task ResolveAsync_WithMalformedDependencyVersionRange_ThrowsNamedInvalidRangeDiagnostic()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "latest");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None));

        Assert.Contains("invalid version range 'latest'", exception.Message);
        Assert.Contains("Plugin.Dependency", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_DependencyProvidedByHost_DoesNotAcquireDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Nuplane.Abstractions", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    // Microsoft.Extensions.* was part of the removed hard-coded IsSharedHostContractPackage
    // allowlist (valence-works/nuplane#90). With no Nuplane:HostProvidedPackages configured, the
    // resolver's default entries are Nuplane's own contract ids only, so this product-specific
    // prefix no longer skips acquisition by default.
    [Fact]
    public async Task ResolveAsync_MicrosoftExtensionsDependency_WithoutHostProvidedPackagesConfigured_AcquiresDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Microsoft.Extensions.Options", dependencyVersionRange: "[8.0.0]");
        var dependency = CreateInstalledPackage("Microsoft.Extensions.Options", "8.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Extensions.Options"] = dependency
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Single(resolver.Requests, static request => request.Id == "Microsoft.Extensions.Options");
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Microsoft.Extensions.Options" && node.Role == PackageNodeRole.Dependency);
    }

    // Elsa.* was part of the removed hard-coded IsSharedHostContractPackage allowlist
    // (valence-works/nuplane#90). With no Nuplane:HostProvidedPackages configured, this
    // product-specific id no longer skips acquisition by default.
    [Fact]
    public async Task ResolveAsync_ElsaFrameworkDependency_WithoutHostProvidedPackagesConfigured_AcquiresDependencyNode()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Elsa.Api.Common", dependencyVersionRange: "[3.7.0-rc1]");
        var dependency = CreateInstalledPackage("Elsa.Api.Common", "3.7.0-rc1");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Elsa.Api.Common"] = dependency
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Single(resolver.Requests, static request => request.Id == "Elsa.Api.Common");
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Elsa.Api.Common" && node.Role == PackageNodeRole.Dependency);
    }

    [Fact]
    public async Task ResolveAsync_DefaultHostProvidedPackages_SkipsNuplaneLoadingAbstractionsDependency()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Nuplane.Loading.Abstractions", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredExactHostProvidedPackageEntry_SkipsMatchingDependencyCaseInsensitively()
    {
        var options = WithEntries("acme.contracts");

        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Acme.Contracts", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy(), options);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredPrefixHostProvidedPackageEntry_SkipsDependenciesUnderThatPrefixOnly()
    {
        var options = WithEntries("ACME.");

        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependenciesXml: """
                <dependencies>
                  <dependency id="Acme.Widgets" version="[1.0.0]" />
                  <dependency id="AcmeToolkit" version="[1.0.0]" />
                </dependencies>
                """);
        var acmeToolkit = CreateInstalledPackage("AcmeToolkit", "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["AcmeToolkit"] = acmeToolkit
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy(), options);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        // "ACME." matches "Acme.Widgets" (case-insensitive prefix) but not "AcmeToolkit": the
        // dependency id must start with the literal prefix, dot included, not merely the letters.
        Assert.Single(resolver.Requests, static request => request.Id == "AcmeToolkit");
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.DoesNotContain(graph.Nodes, static node => node.PackageId == "Acme.Widgets");
        Assert.Contains(graph.Nodes, static node => node.PackageId == "AcmeToolkit");
    }

    [Fact]
    public async Task ResolveAsync_EmptyHostProvidedPackagesList_DependencyInHostDepsJsonIsStillSkipped()
    {
        // Microsoft.Extensions.Options is an ordinary transitive dependency of this test host and
        // therefore appears in its own *.deps.json. With an explicitly empty
        // Nuplane:HostProvidedPackages list (no declared ids or prefixes at all), the separate
        // deps.json rule is the only thing left that can skip it, and it does: valence-works/nuplane#90
        // keeps that rule exactly as it was.
        var options = WithEntries();

        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Microsoft.Extensions.Options", dependencyVersionRange: "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy(), options);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    // The exact entries the removed IsSharedHostContractPackage allowlist matched, verbatim, so a
    // host that relied on any of them can paste this list into Nuplane:HostProvidedPackages and
    // reproduce the previous skip decisions exactly (valence-works/nuplane#90 breaking-change note).
    private static readonly IReadOnlyList<string> LegacySharedHostContractEntries =
    [
        "CShells.Abstractions",
        "CShells.AspNetCore.Abstractions",
        "CShells.FastEndpoints.Abstractions",
        "Nuplane.Abstractions",
        "Nuplane.Loading.Abstractions",
        "Elsa.Api.Common",
        "Elsa.Caching",
        "Elsa.Common",
        "Elsa.Expressions",
        "Elsa.Features",
        "Elsa.KeyValues",
        "Elsa.Mediator",
        "Elsa.Resilience",
        "Elsa.Resilience.Core",
        "Elsa.Tenants",
        "Elsa.Workflows.Core",
        "Elsa.Workflows.Management",
        "Elsa.Workflows.Runtime",
        "Microsoft.Extensions."
    ];

    [Theory]
    [InlineData("CShells.Abstractions")]
    [InlineData("CShells.AspNetCore.Abstractions")]
    [InlineData("CShells.FastEndpoints.Abstractions")]
    [InlineData("Elsa.Api.Common")]
    [InlineData("Elsa.Workflows.Runtime")]
    [InlineData("Microsoft.Extensions.Options")]
    public async Task ResolveAsync_LegacySharedHostContractEntriesConfigured_ReproducesPreviousSkipDecision(string dependencyId)
    {
        var options = WithEntries(LegacySharedHostContractEntries.ToArray());

        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: dependencyId, dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy(), options);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task ResolveAsync_LegacySharedHostContractEntriesConfigured_UnrelatedDependencyIsStillAcquired()
    {
        var options = WithEntries(LegacySharedHostContractEntries.ToArray());

        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Contoso.Widgets", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Contoso.Widgets", "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso.Widgets"] = dependency
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy(), options);

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Single(resolver.Requests, static request => request.Id == "Contoso.Widgets");
    }

    [Fact]
    public async Task ResolveAsync_DeclaredHostProvidedDependencySatisfiedByHostVersion_SkipsItWithoutRefusal()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Elsa.Workflows.Core", dependencyVersionRange: "[4.1.0, 5.0.0)");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = CreateResolverWithHost(resolver, WithEntries("Elsa."), ("Elsa.Workflows.Core", "4.1.2"));

        var result = await ResolveRootAsync(sut, root);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Empty(result.HostVersionRefusals);
        Assert.Empty(result.UnverifiedHostProvidedDependencies);
    }

    [Fact]
    public async Task ResolveAsync_DeclaredHostProvidedDependencyNotSatisfiedByHostVersion_RefusesDependent()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Elsa.Workflows.Core", dependencyVersionRange: "[4.1.0, 5.0.0)");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = CreateResolverWithHost(resolver, WithEntries("Elsa."), ("Elsa.Workflows.Core", "4.0.0"));

        var result = await ResolveRootAsync(sut, root);

        Assert.Empty(resolver.Requests);
        var refusal = Assert.Single(result.HostVersionRefusals);
        Assert.Equal(new HostProvidedDependency("Plugin.Root", "1.0.0", "Elsa.Workflows.Core", "[4.1.0, 5.0.0)", "4.0.0"), refusal.Dependency);
        Assert.Equal(["Plugin.Root"], refusal.RootPackageIds);
        Assert.Contains("'Plugin.Root@1.0.0'", refusal.Message);
        Assert.Contains("'Elsa.Workflows.Core [4.1.0, 5.0.0)'", refusal.Message);
        Assert.Contains("'Elsa.Workflows.Core 4.0.0'", refusal.Message);
    }

    [Fact]
    public async Task ResolveAsync_UndeclaredDependencyInHostAtNonSatisfyingVersion_AcquiresItWithoutRefusal()
    {
        // Only a declared package is held to the host's version. An undeclared one the host carries
        // at a version outside the range is acquired as before, so isolated loading can give the
        // dependent its own copy.
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Contoso.Json", dependencyVersionRange: "[13.0.0,)");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso.Json"] = CreateInstalledPackage("Contoso.Json", "13.0.0")
        });
        var sut = CreateResolverWithHost(resolver, WithEntries(), ("Contoso.Json", "12.0.0"));

        var result = await ResolveRootAsync(sut, root);

        Assert.Single(resolver.Requests, static request => request.Id == "Contoso.Json");
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Contoso.Json" && node.Version == "13.0.0" && node.Role == PackageNodeRole.Dependency);
        Assert.Empty(result.HostVersionRefusals);
        Assert.Empty(result.UnverifiedHostProvidedDependencies);
    }

    [Fact]
    public async Task ResolveAsync_DeclaredHostProvidedDependencyWithUnknownHostVersion_TreatsItAsSatisfiedAndReportsIt()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Elsa.Workflows.Core", dependencyVersionRange: "[4.1.0, 5.0.0)");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase));
        var sut = CreateResolverWithHost(resolver, WithEntries("Elsa."));

        var result = await ResolveRootAsync(sut, root);

        Assert.Empty(resolver.Requests);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Single(graph.Nodes);
        Assert.Empty(result.HostVersionRefusals);
        var unverified = Assert.Single(result.UnverifiedHostProvidedDependencies);
        Assert.Equal(new HostProvidedDependency("Plugin.Root", "1.0.0", "Elsa.Workflows.Core", "[4.1.0, 5.0.0)", HostVersion: null), unverified);
    }

    [Fact]
    public async Task ResolveAsync_TransitiveDependentOfUnsatisfiedHostDependency_RefusesEveryRootThatReachesIt()
    {
        var roots = new[]
        {
            CreateInstalledPackage("Root.A", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "[1.0.0]"),
            CreateInstalledPackage("Root.B", "1.0.0", dependencyId: "Plugin.Shared", dependencyVersionRange: "[1.0.0]"),
            CreateInstalledPackage("Root.C", "1.0.0")
        }.ToDictionary(static root => root.Id, StringComparer.OrdinalIgnoreCase);
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plugin.Shared"] = CreateInstalledPackage("Plugin.Shared", "1.0.0", dependencyId: "Elsa.Workflows.Core", dependencyVersionRange: "[4.1.0, 5.0.0)")
        });
        var sut = CreateResolverWithHost(resolver, WithEntries("Elsa."), ("Elsa.Workflows.Core", "4.0.0"));

        var result = await sut.ResolveAsync(
            roots.Keys.Select(static id => new PackageRequest(id, "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")).ToArray(),
            (request, _) => Task.FromResult(roots[request.Id]),
            CancellationToken.None);

        var refusal = Assert.Single(result.HostVersionRefusals);
        Assert.Equal("Plugin.Shared", refusal.Dependency.DependentPackageId);
        Assert.Equal(["Root.A", "Root.B"], refusal.RootPackageIds);
    }

    [Fact]
    public async Task ResolveAsync_DependencyAlreadyLoadedAsPlugin_StillAcquiresDependencyNode()
    {
        var loadedDependencyId = $"Plugin.LoadedDependency.{Guid.NewGuid():N}";
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(loadedDependencyId), AssemblyBuilderAccess.Run);
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: loadedDependencyId, dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage(loadedDependencyId, "1.0.0");
        var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            [loadedDependencyId] = dependency
        });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, node => string.Equals(node.PackageId, loadedDependencyId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(resolver.Requests, request => string.Equals(request.Id, loadedDependencyId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_DependencyDllExistsInAppBase_StillAcquiresDependencyNode()
    {
        var dependencyId = $"Plugin.AppBaseDependency.{Guid.NewGuid():N}";
        var appBaseDll = Path.Combine(AppContext.BaseDirectory, $"{dependencyId}.dll");
        File.WriteAllBytes(appBaseDll, []);
        try
        {
            var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: dependencyId, dependencyVersionRange: "[1.0.0]");
            var dependency = CreateInstalledPackage(dependencyId, "1.0.0");
            var resolver = new StubPackageResolver(new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [dependencyId] = dependency
            });
            var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

            var result = await sut.ResolveAsync(
                [new PackageRequest("Plugin.Root", "[1.0.0]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
                (_, _) => Task.FromResult(root),
                CancellationToken.None);

            var graph = Assert.Single(result.ResolvedGraphs);
            Assert.Contains(graph.Nodes, node => string.Equals(node.PackageId, dependencyId, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(resolver.Requests, request => string.Equals(request.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(appBaseDll);
        }
    }


    [Fact]
    public async Task ResolveAsync_WithFrameworkSpecificDependencyGroups_SelectsCompatibleHostGroupOnly()
    {
        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependenciesXml: """
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="Plugin.Compatible" version="[1.0.0]" />
                  </group>
                  <group targetFramework="net472">
                    <dependency id="Plugin.Legacy" version="[1.0.0]" />
                  </group>
                </dependencies>
                """);
        var compatible = CreateInstalledPackage("Plugin.Compatible", "1.0.0");
        var legacy = CreateInstalledPackage("Plugin.Legacy", "1.0.0");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Compatible"] = compatible,
                ["Plugin.Legacy"] = legacy
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "Plugin.Compatible");
        Assert.DoesNotContain(graph.Nodes, static node => node.PackageId == "Plugin.Legacy");
        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Plugin.Compatible", edge.ToPackageId);
    }

    [Theory]
    [InlineData(".NETStandard2.0")]
    [InlineData(".NETStandard,Version=v2.0")]
    public async Task ResolveAsync_WithNuspecNetStandardDependencyGroup_SelectsCompatibleGroup(string targetFramework)
    {
        var root = CreateInstalledPackage(
            "SQLitePCLRaw.bundle_e_sqlite3",
            "2.1.11",
            dependenciesXml: $$"""
                <dependencies>
                  <group targetFramework="{{targetFramework}}">
                    <dependency id="SQLitePCLRaw.provider.e_sqlite3" version="2.1.11" />
                    <dependency id="SQLitePCLRaw.lib.e_sqlite3" version="2.1.11" />
                  </group>
                </dependencies>
                """);
        var provider = CreateInstalledPackage("SQLitePCLRaw.provider.e_sqlite3", "2.1.11");
        var nativeLibrary = CreateInstalledPackage("SQLitePCLRaw.lib.e_sqlite3", "2.1.11");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["SQLitePCLRaw.provider.e_sqlite3"] = provider,
                ["SQLitePCLRaw.lib.e_sqlite3"] = nativeLibrary
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("SQLitePCLRaw.bundle_e_sqlite3", "[2.1.11]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Contains(graph.Nodes, static node => node.PackageId == "SQLitePCLRaw.provider.e_sqlite3");
        Assert.Contains(graph.Nodes, static node => node.PackageId == "SQLitePCLRaw.lib.e_sqlite3");
        Assert.Contains(graph.Edges, static edge => edge.ToPackageId == "SQLitePCLRaw.provider.e_sqlite3");
        Assert.Contains(graph.Edges, static edge => edge.ToPackageId == "SQLitePCLRaw.lib.e_sqlite3");
    }

    [Fact]
    public async Task ResolveAsync_NativeOnlyRidDependency_RemainsResolvedWithRuntimeAsset()
    {
        var root = CreateInstalledPackage(
            "Plugin.Root",
            "1.0.0",
            dependencyId: "Microsoft.Data.SqlClient.SNI.runtime",
            dependencyVersionRange: "[6.0.2]");
        var nativeDependency = CreateInstalledPackage("Microsoft.Data.SqlClient.SNI.runtime", "6.0.2");
        var nativeAssetPath = Path.Combine(
            nativeDependency.InstallPath,
            "runtimes",
            "win-x64",
            "native",
            "Microsoft.Data.SqlClient.SNI.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(nativeAssetPath)!);
        File.WriteAllText(nativeAssetPath, "native asset");

        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [nativeDependency.Id] = nativeDependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var result = await sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

        Assert.Contains(result.ResolvedPackages, package => package.Id == nativeDependency.Id);
        var graph = Assert.Single(result.ResolvedGraphs);
        var node = Assert.Single(graph.Nodes, package => package.PackageId == nativeDependency.Id);
        Assert.Equal([Path.Combine("runtimes", "win-x64", "native", "Microsoft.Data.SqlClient.SNI.dll")], node.RuntimeAssets);
        Assert.Equal(node.RuntimeAssets, node.SupportAssets);
        Assert.Empty(node.DiscoverableAssets);
    }

    [Fact]
    public async Task ResolveAsync_WithDependencyCycle_ThrowsCycleDiagnostic()
    {
        var root = CreateInstalledPackage("Plugin.Root", "1.0.0", dependencyId: "Plugin.Dependency", dependencyVersionRange: "[1.0.0]");
        var dependency = CreateInstalledPackage("Plugin.Dependency", "1.0.0", dependencyId: "Plugin.Root", dependencyVersionRange: "[1.0.0]");
        var resolver = new StubPackageResolver(
            new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Root"] = root,
                ["Plugin.Dependency"] = dependency
            });
        var sut = new PackageDependencyGraphResolver(resolver, new PassthroughRetryPolicy());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResolveAsync(
            [new PackageRequest("Plugin.Root", "[1.0.0]", "test-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None));

        Assert.Contains("Dependency cycle detected", exception.Message);
        Assert.Contains("Plugin.Root@1.0.0 -> Plugin.Dependency@1.0.0 -> Plugin.Root@1.0.0", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static PackageDependencyGraphResolver CreateResolverWithHost(
        StubPackageResolver resolver,
        HostProvidedPackagesOptions options,
        params (string PackageId, string Version)[] hostPackages) =>
        new(
            resolver,
            new PassthroughRetryPolicy(),
            options,
            hostPackages.ToDictionary(static package => package.PackageId, static package => package.Version, StringComparer.OrdinalIgnoreCase));

    private static Task<PackageDependencyGraphResolutionResult> ResolveRootAsync(PackageDependencyGraphResolver sut, ResolvedPackage root) =>
        sut.ResolveAsync(
            [new PackageRequest(root.Id, $"[{root.Version}]", "root-feed", PackageUpdatePolicy.Exact, "test-source")],
            (_, _) => Task.FromResult(root),
            CancellationToken.None);

    private ResolvedPackage CreateInstalledPackage(
        string packageId,
        string version,
        string? dependencyId = null,
        string? dependencyVersionRange = null,
        string? dependenciesXml = null)
    {
        var installPath = Path.Combine(_tempRoot, packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, $"{packageId}.nuspec"), CreateNuspec(packageId, version, dependencyId, dependencyVersionRange, dependenciesXml));
        return new ResolvedPackage(packageId, version, "test-feed", installPath, DateTimeOffset.UtcNow, "test-source");
    }

    private static string CreateNuspec(
        string packageId,
        string version,
        string? dependencyId,
        string? dependencyVersionRange,
        string? dependenciesXml) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>test</authors>
            <description>Test package</description>
            {{dependenciesXml ?? CreateDependencies(dependencyId, dependencyVersionRange)}}
          </metadata>
        </package>
        """;

    private static string CreateDependencies(string? dependencyId, string? dependencyVersionRange) =>
        string.IsNullOrWhiteSpace(dependencyId) || string.IsNullOrWhiteSpace(dependencyVersionRange)
            ? string.Empty
            : $"<dependencies><dependency id=\"{dependencyId}\" version=\"{dependencyVersionRange}\" /></dependencies>";

    private sealed class StubPackageResolver(IReadOnlyDictionary<string, ResolvedPackage> packages) : IPackageResolver
    {
        public List<PackageRequest> Requests { get; } = [];

        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return packages.TryGetValue(request.Id, out var package)
                ? Task.FromResult(package)
                : Task.FromException<ResolvedPackage>(new InvalidOperationException($"Package '{request.Id}' was not configured."));
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}
