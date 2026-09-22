using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Activation;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Resolution with a desired-state contributor in it: what a selected capability adds to the root
/// set, what a refusal takes out of it, and where the fixpoint stops. Every package is a real
/// install directory; the resolver is local and contacts nothing.
/// </summary>
public sealed class PackageApplyExecutorCapabilityTests : IDisposable
{
    private const string Capability = "ef-provider";
    private const string ModuleId = "Acme.Module";
    private const string PostgreSqlEngineId = "Acme.Engine.PostgreSql";
    private const string SqliteEngineId = "Acme.Engine.Sqlite";
    private const string CorrelationId = "corr-capability-executor";

    private readonly InstalledPackageStore _packages = new();
    private readonly Dictionary<string, List<ResolvedPackage>> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CapabilityOptions _capabilityOptions = new();
    private readonly ReconciliationOptions _reconciliationOptions = new();
    private readonly CapabilityContributionLedger _ledger = new();
    private readonly RecordingReconciliationLogger _logger = new();
    private readonly RecordingFailureRecorder _recorder = new();
    private readonly RecordingPackageMetadataReader _reader = new();

    public void Dispose() => _packages.Dispose();

    [Fact]
    public async Task ResolveAsync_WithASelectedOption_AddsItAsARootWithACapabilitySourceName()
    {
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId));

        var engine = Assert.Single(result.ResolvedPackages, package => package.Id == PostgreSqlEngineId);
        Assert.Equal("10.0.0", engine.Version);
        Assert.Equal($"capability:{Capability}=PostgreSql", engine.SourceName);
        Assert.Empty(result.FailedPackageIds);

        var graph = Assert.Single(result.ResolvedGraphs);
        var node = Assert.Single(graph.Nodes, node => node.PackageId == PostgreSqlEngineId);
        Assert.Equal(PackageNodeRole.Root, node.Role);
        Assert.Contains(PostgreSqlEngineId, graph.Roots.Select(static root => root.PackageId));

        // Discoverable assets are what make a root visible to feature discovery, and are exactly
        // what a mere dependency would not have: "as a root" is the whole point of a capability.
        Assert.Equal([Path.Combine("lib", "net10.0", $"{PostgreSqlEngineId}.dll")], node.DiscoverableAssets);
        Assert.Empty(node.SupportAssets);
    }

    [Fact]
    public async Task ResolveAsync_WithNoSelection_FailsTheDeclaringPackageAndInstallsNoOption()
    {
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId));

        Assert.Equal(ModuleId, Assert.Single(result.FailedPackageIds));
        Assert.DoesNotContain(PostgreSqlEngineId, result.ResolvedPackages.Select(static package => package.Id));
        Assert.DoesNotContain(PostgreSqlEngineId, _resolvedRequestIds);
        var message = _recorder.MessageFor(ModuleId, "capability-unselected");
        Assert.Contains("PostgreSql", message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", message, StringComparison.Ordinal);
        Assert.Contains($"Nuplane:Capabilities:{Capability}", message, StringComparison.Ordinal);
        Assert.Equal((ModuleId, "capability-unselected", message), Assert.Single(_logger.Refused));
    }

    [Fact]
    public async Task ResolveAsync_WithNoSelection_DropsTheDeclaringPackageFromTheAppliedSet()
    {
        DeclareModule();
        Install("Acme.Unrelated");

        var result = await ResolveAsync(Root(ModuleId), Root("Acme.Unrelated"));

        Assert.Equal(["Acme.Unrelated"], result.ResolvedPackages.Select(static package => package.Id));
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(["Acme.Unrelated"], graph.Nodes.Select(static node => node.PackageId));
    }

    [Fact]
    public async Task ResolveAsync_WithAnUnknownSelectedOption_FailsTheDeclaringPackage()
    {
        Select("Postgres");
        DeclareModule();

        var result = await ResolveAsync(Root(ModuleId));

        Assert.Equal(ModuleId, Assert.Single(result.FailedPackageIds));
        Assert.Contains("Postgres", _recorder.MessageFor(ModuleId, "capability-unknown-option"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WithAnExplicitRootForTheSelectedOption_AddsNothingAndKeepsBothRoots()
    {
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId), Root(PostgreSqlEngineId, "[10.0.0]"));

        Assert.Empty(result.FailedPackageIds);
        var engine = Assert.Single(result.ResolvedPackages, package => package.Id == PostgreSqlEngineId);
        Assert.Equal("test-source", engine.SourceName);
        Assert.Equal(
            [PostgreSqlEngineId, ModuleId],
            result.ResolvedPackages.Select(static package => package.Id).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_WithAnExplicitRootAtANonSatisfyingVersion_AppliesTheRootButNotTheModule()
    {
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "9.0.0");

        var result = await ResolveAsync(Root(ModuleId), Root(PostgreSqlEngineId, "[9.0.0]"));

        Assert.Equal(ModuleId, Assert.Single(result.FailedPackageIds));
        Assert.Equal([PostgreSqlEngineId], result.ResolvedPackages.Select(static package => package.Id));
        var message = _recorder.MessageFor(ModuleId, "capability-conflict");
        Assert.Contains("[9.0.0]", message, StringComparison.Ordinal);
        Assert.Contains("[10.0.0]", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WithTwoDeclaringRoots_AddsTheOptionOnce()
    {
        Select("PostgreSql");
        DeclareModule();
        DeclareModule("Acme.OtherModule");
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId), Root("Acme.OtherModule"));

        Assert.Empty(result.FailedPackageIds);
        Assert.Single(result.ResolvedPackages, package => package.Id == PostgreSqlEngineId);
        Assert.Single(_resolvedRequestIds, id => id == PostgreSqlEngineId);
    }

    [Fact]
    public async Task ResolveAsync_WhenOnlyADependencyDeclaresTheCapability_HonoursItsDeclaration()
    {
        Select("PostgreSql");
        Install("Acme.Host", dependency: (ModuleId, "[1.0.0]"));
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root("Acme.Host"));

        Assert.Empty(result.FailedPackageIds);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(PackageNodeRole.Dependency, Assert.Single(graph.Nodes, node => node.PackageId == ModuleId).Role);
        Assert.Equal(PackageNodeRole.Root, Assert.Single(graph.Nodes, node => node.PackageId == PostgreSqlEngineId).Role);
    }

    [Fact]
    public async Task ResolveAsync_WhenOnlyADependencyDeclaresAnUnselectedCapability_FailsItLoudlyWithoutFailingItsParent()
    {
        // A refused package that is not a root cannot be dropped from the root set, so it stays in
        // the closure as its parent's dependency — where it is not discoverable. What must not happen
        // is that the refusal disappears: it is recorded, reported, and degrades the cycle.
        Install("Acme.Host", dependency: (ModuleId, "[1.0.0]"));
        DeclareModule();

        var result = await ResolveAsync(Root("Acme.Host"));

        Assert.Equal(ModuleId, Assert.Single(result.FailedPackageIds));
        Assert.Contains($"Nuplane:Capabilities:{Capability}", _recorder.MessageFor(ModuleId, "capability-unselected"), StringComparison.Ordinal);
        var refused = Assert.Single(_logger.Refused);
        Assert.Equal((ModuleId, "capability-unselected"), (refused.PackageId, refused.Stage));
        Assert.Contains("Acme.Host", result.ResolvedPackages.Select(static package => package.Id));
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(PackageNodeRole.Dependency, Assert.Single(graph.Nodes, node => node.PackageId == ModuleId).Role);
    }

    [Fact]
    public async Task ResolveAsync_WhenAContributedRootItselfDeclaresACapability_ReachesTheFixpoint()
    {
        Select("PostgreSql");
        _capabilityOptions.Selections["engine-driver"] = new() { Options = ["Native"] };
        DeclareModule();
        Install(
            PostgreSqlEngineId,
            "10.0.0",
            InstalledPackageStore.CapabilityMetadata("engine-driver", ("Native", "Acme.Driver.Native", "[3.0.0]")));
        Install("Acme.Driver.Native", "3.0.0");

        var result = await ResolveAsync(Root(ModuleId));

        Assert.Empty(result.FailedPackageIds);
        var graph = Assert.Single(result.ResolvedGraphs);
        Assert.Equal(
            ["Acme.Driver.Native", PostgreSqlEngineId, ModuleId],
            graph.Roots.Select(static root => root.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            "capability:engine-driver=Native",
            Assert.Single(result.ResolvedPackages, package => package.Id == "Acme.Driver.Native").SourceName);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheContributionChainOutgrowsTheBound_RefusesTheChainWithoutAcquiringMore()
    {
        // A chain of declaring packages, each selecting the next: link 0 is the desired root, and
        // every link after it is contributed by the one before.
        var chainLength = PackageApplyExecutor.MaxContributionRounds + 2;
        for (var link = 0; link < chainLength; link++)
        {
            var capability = $"link-{link}";
            _capabilityOptions.Selections[capability] = new() { Options = ["Next"] };
            Install(
                ChainId(link),
                metadata: InstalledPackageStore.CapabilityMetadata(capability, ("Next", ChainId(link + 1), "[1.0.0]")));
        }

        var result = await ResolveAsync(Root(ChainId(0)));

        var lastAcquired = ChainId(PackageApplyExecutor.MaxContributionRounds);
        var refused = ChainId(PackageApplyExecutor.MaxContributionRounds + 1);
        Assert.Contains(lastAcquired, result.FailedPackageIds);
        Assert.Contains(refused, result.FailedPackageIds);
        Assert.DoesNotContain(refused, _resolvedRequestIds);
        var message = _recorder.MessageFor(refused, "capability-contribution-limit");
        Assert.Contains($"within {PackageApplyExecutor.MaxContributionRounds} rounds", message, StringComparison.Ordinal);
        Assert.Contains($"{ChainId(1)} (capability:link-0=Next)", message, StringComparison.Ordinal);
        Assert.Contains($"{refused} (capability:link-{PackageApplyExecutor.MaxContributionRounds}=Next)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheSelectedOptionCannotBeAcquired_FailsTheOptionAndItsDeclaringPackages()
    {
        Select("PostgreSql");
        DeclareModule();
        DeclareModule("Acme.OtherModule");

        var result = await ResolveAsync(Root(ModuleId), Root("Acme.OtherModule"));

        Assert.Equal(
            [PostgreSqlEngineId, ModuleId, "Acme.OtherModule"],
            result.FailedPackageIds.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(result.ResolvedPackages);
        Assert.Contains(_recorder.Records, record => record.PackageId == PostgreSqlEngineId && record.Stage == "resolve");
        foreach (var declaringPackageId in new[] { ModuleId, "Acme.OtherModule" })
        {
            Assert.Contains(
                PostgreSqlEngineId,
                _recorder.MessageFor(declaringPackageId, "capability-unresolved"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithRequirePinnedContributions_RefusesBeforeResolvingTheContribution()
    {
        _reconciliationOptions.RequirePinnedContributions = true;
        Select("PostgreSql");
        Install(
            ModuleId,
            metadata: InstalledPackageStore.CapabilityMetadata(Capability, ("PostgreSql", PostgreSqlEngineId, "[10.0.0,11.0.0)")));
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId));

        Assert.Equal(ModuleId, Assert.Single(result.FailedPackageIds));
        Assert.DoesNotContain(PostgreSqlEngineId, _resolvedRequestIds);
        Assert.Empty(result.ResolvedPackages);
        Assert.Contains("[10.0.0,11.0.0)", _recorder.MessageFor(ModuleId, "capability-unpinned"), StringComparison.Ordinal);
        Assert.Equal(PostgreSqlEngineId, Assert.Single(_ledger.UnpinnedRequests).Id);
    }

    [Fact]
    public async Task ResolveAsync_WhenAContributorThrows_PropagatesAndAppliesNothing()
    {
        // The dangerous alternative is a caught exception: "could not work out what this package
        // requires" would then be indistinguishable from "it required nothing", and the cycle would
        // report a healthy closure that is missing a root. So the cycle fails loudly instead —
        // nothing is resolved as a contribution, nothing is recorded as a failure that a degraded
        // result would later have to explain, and no transaction runs because ResolveAsync never
        // returns a resolution to apply.
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");
        var contributor = new ThrowingContributor();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResolveWithAsync(contributor, Root(ModuleId)));

        Assert.Equal(ThrowingContributor.Message, ex.Message);
        Assert.True(contributor.WasCalled);
        Assert.Empty(_recorder.Records);
        Assert.DoesNotContain(PostgreSqlEngineId, _resolvedRequestIds);
        Assert.Equal([ModuleId], _resolvedRequestIds);
    }

    [Fact]
    public async Task ResolveAsync_OverTwoContributionRounds_ReadsEachPackagesMetadataOnce()
    {
        // The cycle runs a second round to confirm the fixpoint, over a closure that now also holds
        // the contributed root. Re-reading and re-validating every package's nuplane.json per round
        // is work that grows with the closure for no new information.
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");

        var result = await ResolveAsync(Root(ModuleId));

        Assert.Empty(result.FailedPackageIds);
        Assert.Equal(1, _reader.CountFor(ModuleId));
        Assert.Equal(1, _reader.CountFor(PostgreSqlEngineId));
    }

    [Fact]
    public async Task ResolveAsync_WithNoContributorsRegistered_ExpandsTheClosureExactlyOnce()
    {
        Select("PostgreSql");
        DeclareModule();
        Install(PostgreSqlEngineId, "10.0.0");
        var resolver = new VersionRangePackageResolver(Candidates());
        var executor = new PackageApplyExecutor(
            resolver,
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), _recorder),
            new PassthroughRetryPolicy(),
            _recorder);

        var result = await executor.ResolveAsync([Root(ModuleId)], CorrelationId, CancellationToken.None);

        Assert.Equal([ModuleId], result.ResolvedPackages.Select(static package => package.Id));
        Assert.Empty(result.FailedPackageIds);
        Assert.Equal([ModuleId], resolver.Requests.Select(static request => request.Id));
    }

    private static string ChainId(int link) => $"Acme.Chain{link}";

    private void Select(params string[] options) =>
        _capabilityOptions.Selections[Capability] = new() { Options = options };

    private void DeclareModule(string packageId = ModuleId) =>
        Install(
            packageId,
            metadata: InstalledPackageStore.CapabilityMetadata(
                Capability,
                ("PostgreSql", PostgreSqlEngineId, "[10.0.0]"),
                ("Sqlite", SqliteEngineId, "[10.0.10]")));

    /// <summary>Installs a package and makes it resolvable, the way a feed holding it would.</summary>
    private void Install(
        string packageId,
        string version = "1.0.0",
        string? metadata = null,
        (string Id, string VersionRange)? dependency = null)
    {
        if (!_candidates.TryGetValue(packageId, out var candidates))
        {
            candidates = [];
            _candidates[packageId] = candidates;
        }

        candidates.Add(_packages.Install(packageId, version, metadata, dependency));
    }

    private static PackageRequest Root(string packageId, string versionRange = "[1.0.0]") =>
        new(packageId, versionRange, null, PackageUpdatePolicy.Exact, "test-source");

    private readonly List<string> _resolvedRequestIds = [];

    private Dictionary<string, IReadOnlyList<ResolvedPackage>> Candidates() =>
        _candidates.ToDictionary(
            static candidate => candidate.Key,
            static candidate => (IReadOnlyList<ResolvedPackage>)candidate.Value,
            StringComparer.OrdinalIgnoreCase);

    private Task<PackageResolutionResult> ResolveAsync(params PackageRequest[] desiredRequests) =>
        ResolveWithAsync(
            new CapabilityDesiredStateContributor(
                new OptionsWrapper<CapabilityOptions>(_capabilityOptions),
                new OptionsWrapper<ReconciliationOptions>(_reconciliationOptions),
                _ledger,
                _logger,
                _reader),
            desiredRequests);

    private async Task<PackageResolutionResult> ResolveWithAsync(
        IDesiredStateContributor contributor,
        params PackageRequest[] desiredRequests)
    {
        var resolver = new VersionRangePackageResolver(Candidates());
        var executor = new PackageApplyExecutor(
            new SourceStampingResolver(resolver),
            new PackageTransactionCoordinator(new AtomicPointerSwitcher(), _recorder),
            new PassthroughRetryPolicy(),
            _recorder,
            [contributor],
            _logger);

        try
        {
            return await executor.ResolveAsync(desiredRequests, CorrelationId, CancellationToken.None);
        }
        finally
        {
            _resolvedRequestIds.AddRange(resolver.Requests.Select(static request => request.Id));
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    /// <summary>
    /// A contributor that cannot answer at all — the shape a faulty host-supplied contributor has.
    /// </summary>
    private sealed class ThrowingContributor : IDesiredStateContributor
    {
        internal const string Message = "This contributor could not determine what the closure requires.";

        public bool WasCalled { get; private set; }

        public Task<DesiredStateContribution> ContributeAsync(DesiredStateContributionContext context, CancellationToken ct)
        {
            WasCalled = true;
            throw new InvalidOperationException(Message);
        }
    }

    /// <summary>
    /// Stamps the requesting source's name onto the resolved package, which is what
    /// <c>MultiFeedPackageResolver</c> does and what makes a contributed root's provenance visible
    /// downstream.
    /// </summary>
    private sealed class SourceStampingResolver(IPackageResolver inner) : IPackageResolver
    {
        public async Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken) =>
            await inner.ResolveAsync(request, cancellationToken) with { SourceName = request.SourceName };
    }
}
