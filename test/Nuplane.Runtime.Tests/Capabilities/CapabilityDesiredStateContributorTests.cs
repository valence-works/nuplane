using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Capabilities;

/// <summary>
/// The contributor's own contract: which packages' <c>nuplane.json</c> it reads, what it refuses,
/// and what it hands the resolution executor. Every package here is a real install directory.
/// </summary>
public sealed class CapabilityDesiredStateContributorTests : IDisposable
{
    private const string Capability = "ef-provider";
    private const string EnginePackageId = "Acme.Engine.PostgreSql";
    private const string OtherEnginePackageId = "Acme.Engine.Sqlite";
    private const string CorrelationId = "corr-capability";

    private readonly InstalledPackageStore _packages = new();
    private readonly CapabilityOptions _capabilityOptions = new();
    private readonly ReconciliationOptions _reconciliationOptions = new();
    private readonly CapabilityContributionLedger _ledger = new();
    private readonly RecordingReconciliationLogger _logger = new();
    private readonly CapabilityDesiredStateContributor _sut;

    public CapabilityDesiredStateContributorTests() =>
        _sut = new(
            new OptionsWrapper<CapabilityOptions>(_capabilityOptions),
            new OptionsWrapper<ReconciliationOptions>(_reconciliationOptions),
            _ledger,
            _logger);

    public void Dispose() => _packages.Dispose();

    [Fact]
    public async Task ContributeAsync_WhenTheSelectedOptionIsDeclared_ContributesItAsARootForItsDeclaringPackage()
    {
        Select("PostgreSql");
        var module = DeclaringModule();

        var contribution = await ContributeAsync([module]);

        var request = Assert.Single(contribution.Requests);
        Assert.Equal(EnginePackageId, request.Request.Id);
        Assert.Equal("[10.0.0]", request.Request.VersionRange);
        Assert.Equal(PackageUpdatePolicy.Exact, request.Request.UpdatePolicy);
        Assert.Equal($"capability:{Capability}=PostgreSql", request.Request.SourceName);
        Assert.Equal("Acme.Module", Assert.Single(request.DeclaringPackageIds));
        Assert.Empty(contribution.Refusals);
        Assert.Equal(
            (Capability, "PostgreSql", EnginePackageId, "[10.0.0]"),
            Assert.Single(_logger.Selected));
    }

    [Fact]
    public async Task ContributeAsync_WithNoSelection_RefusesTheDeclaringPackageNamingTheOptionsAndTheKey()
    {
        var module = DeclaringModule();

        var contribution = await ContributeAsync([module]);

        Assert.Empty(contribution.Requests);
        var refusal = Assert.Single(contribution.Refusals);
        Assert.Equal("Acme.Module", refusal.PackageId);
        Assert.Equal("capability-unselected", refusal.Stage);
        Assert.Contains("PostgreSql", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Nuplane:Capabilities:{Capability}", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributeAsync_WithNoCapabilityMetadataAnywhere_ContributesNothing()
    {
        var contribution = await ContributeAsync([_packages.Install("Acme.Plain")]);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
        Assert.Empty(_logger.Refused);
    }

    [Fact]
    public async Task ContributeAsync_WithSchemaOneMetadata_ContributesNothing()
    {
        Select("PostgreSql");

        var contribution = await ContributeAsync([_packages.Install("Acme.Loading", metadata: InstalledPackageStore.LoadingMetadata)]);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
    }

    [Fact]
    public async Task ContributeAsync_WithAnInvalidSchemaTwoDocument_RefusesItsPackage()
    {
        var broken = _packages.Install(
            "Acme.Broken",
            metadata: """
            {
              "schemaVersion": 2,
              "capabilities": [ { "name": "ef-provider", "options": [] } ]
            }
            """);

        var contribution = await ContributeAsync([broken]);

        Assert.Empty(contribution.Requests);
        var refusal = Assert.Single(contribution.Refusals);
        Assert.Equal("Acme.Broken", refusal.PackageId);
        Assert.Equal("capability-metadata-invalid", refusal.Stage);
        Assert.Contains("Acme.Broken@1.0.0", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributeAsync_WithAnInvalidSchemaOneDocument_ContributesNothing()
    {
        // A schema-1 document can only carry load-mode metadata, which never affects the closure, so
        // an invalid one keeps being ignored here exactly as it was before capabilities existed.
        var broken = _packages.Install(
            "Acme.BrokenLoading",
            metadata: """
            { "schemaVersion": 1, "loading": { "loadMode": "NotAMode", "scope": "PackageOnly" } }
            """);

        var contribution = await ContributeAsync([broken]);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
    }

    [Fact]
    public async Task ContributeAsync_WithAnExplicitRootForTheSelectedOption_ContributesNothing()
    {
        Select("PostgreSql");
        var module = DeclaringModule();

        var contribution = await ContributeAsync(
            [module],
            ExplicitRoot("Acme.Module"),
            ExplicitRoot(EnginePackageId, "[10.0.0]"));

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
    }

    [Fact]
    public async Task ContributeAsync_WithAnExplicitRootAndNoSelection_LogsOneSatisfiedLineAndContributesNothing()
    {
        var module = DeclaringModule();
        var context = Context([module], ExplicitRoot("Acme.Module"), ExplicitRoot(EnginePackageId, "[10.0.0]"));

        // Twice, the way the fixpoint calls it: the promised single Information line stays single.
        await _sut.ContributeAsync(context, CancellationToken.None);
        var contribution = await _sut.ContributeAsync(context, CancellationToken.None);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
        Assert.Equal((Capability, EnginePackageId), Assert.Single(_logger.SatisfiedByExplicitRoot));
    }

    [Fact]
    public async Task ContributeAsync_WithAnExplicitRootAtANonSatisfyingVersion_RefusesTheDeclaringPackage()
    {
        Select("PostgreSql");
        var module = DeclaringModule();

        var contribution = await ContributeAsync(
            [module],
            ExplicitRoot("Acme.Module"),
            ExplicitRoot(EnginePackageId, "[9.0.0]"));

        Assert.Empty(contribution.Requests);
        var refusal = Assert.Single(contribution.Refusals);
        Assert.Equal("Acme.Module", refusal.PackageId);
        Assert.Equal("capability-conflict", refusal.Stage);
        Assert.Contains("[9.0.0]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[10.0.0]", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributeAsync_WithTwoPackagesDeclaringTheSameCapability_ContributesOneRootForBoth()
    {
        Select("PostgreSql");

        var contribution = await ContributeAsync([DeclaringModule(), DeclaringModule("Acme.OtherModule")]);

        var request = Assert.Single(contribution.Requests);
        Assert.Equal(["Acme.Module", "Acme.OtherModule"], request.DeclaringPackageIds);
    }

    [Fact]
    public async Task ContributeAsync_WithTwoSelectedOptions_ContributesBothOrderedByOptionName()
    {
        Select("PostgreSql", "Sqlite");

        var contribution = await ContributeAsync([DeclaringModule()]);

        Assert.Equal(
            [$"capability:{Capability}=PostgreSql", $"capability:{Capability}=Sqlite"],
            contribution.Requests.Select(static request => request.Request.SourceName));
    }

    [Fact]
    public async Task ContributeAsync_WhenASelectionMatchesNoDeclaration_LogsItWithoutRefusingAnything()
    {
        _capabilityOptions.Selections["message-broker"] = new() { Options = ["RabbitMq"] };

        var contribution = await ContributeAsync([_packages.Install("Acme.Plain")]);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
        Assert.Equal("message-broker", Assert.Single(_logger.UnmatchedSelections));
    }

    [Fact]
    public async Task ContributeAsync_WithRequirePinnedContributionsAndARangedOption_RefusesBeforeContributingIt()
    {
        _reconciliationOptions.RequirePinnedContributions = true;
        Select("PostgreSql");
        var module = _packages.Install(
            "Acme.Module",
            metadata: InstalledPackageStore.CapabilityMetadata(Capability, ("PostgreSql", EnginePackageId, "[10.0.0,11.0.0)")));

        var contribution = await ContributeAsync([module]);

        Assert.Empty(contribution.Requests);
        var refusal = Assert.Single(contribution.Refusals);
        Assert.Equal("Acme.Module", refusal.PackageId);
        Assert.Equal("capability-unpinned", refusal.Stage);
        var unpinned = Assert.Single(_ledger.UnpinnedRequests);
        Assert.Equal(EnginePackageId, unpinned.Id);
        Assert.Equal("[10.0.0,11.0.0)", unpinned.VersionRange);
        Assert.Equal($"capability:{Capability}=PostgreSql", unpinned.SourceName);
    }

    [Fact]
    public async Task ContributeAsync_WithoutRequirePinnedContributions_ContributesARangedOption()
    {
        Select("PostgreSql");
        var module = _packages.Install(
            "Acme.Module",
            metadata: InstalledPackageStore.CapabilityMetadata(Capability, ("PostgreSql", EnginePackageId, "[10.0.0,11.0.0)")));

        var contribution = await ContributeAsync([module]);

        var request = Assert.Single(contribution.Requests);
        Assert.Equal(PackageUpdatePolicy.Range, request.Request.UpdatePolicy);
        Assert.Empty(_ledger.UnpinnedRequests);
    }

    [Fact]
    public async Task ContributeAsync_ForANewCycle_ClearsThePreviousCyclesUnpinnedContributions()
    {
        _reconciliationOptions.RequirePinnedContributions = true;
        Select("PostgreSql");
        var module = _packages.Install(
            "Acme.Module",
            metadata: InstalledPackageStore.CapabilityMetadata(Capability, ("PostgreSql", EnginePackageId, "[10.0.0,11.0.0)")));
        await ContributeAsync([module]);
        Assert.Single(_ledger.UnpinnedRequests);

        _reconciliationOptions.RequirePinnedContributions = false;
        await _sut.ContributeAsync(
            new("corr-second-cycle", [], [module], []),
            CancellationToken.None);

        Assert.Empty(_ledger.UnpinnedRequests);
    }

    [Fact]
    public async Task ContributeAsync_WithARootAlreadyContributedForTheSelectedOption_ContributesNothingFurther()
    {
        // The fixpoint's terminating round: the option is already a contributed root, so the round
        // adds nothing and the loop ends.
        Select("PostgreSql");
        var module = DeclaringModule();
        var engine = _packages.Install(EnginePackageId, "10.0.0");
        var contributed = new PackageRequest(EnginePackageId, "[10.0.0]", null, PackageUpdatePolicy.Exact, $"capability:{Capability}=PostgreSql");

        var contribution = await _sut.ContributeAsync(
            new(CorrelationId, [ExplicitRoot("Acme.Module")], [module, engine], [contributed]),
            CancellationToken.None);

        Assert.Empty(contribution.Requests);
        Assert.Empty(contribution.Refusals);
    }

    [Fact]
    public async Task ContributeAsync_WithAContributedRootAtANonSatisfyingVersionForAnotherCapability_RefusesThatCapability()
    {
        // Two capabilities that name the same package at different versions: the second is refused
        // rather than silently binding against the version the first one contributed.
        Select("PostgreSql");
        _capabilityOptions.Selections["ef-provider-2"] = new() { Options = ["PostgreSql"] };
        var module = DeclaringModule();
        var otherModule = _packages.Install(
            "Acme.OtherModule",
            metadata: InstalledPackageStore.CapabilityMetadata("ef-provider-2", ("PostgreSql", EnginePackageId, "[9.0.0]")));
        var contributed = new PackageRequest(EnginePackageId, "[10.0.0]", null, PackageUpdatePolicy.Exact, $"capability:{Capability}=PostgreSql");

        var contribution = await _sut.ContributeAsync(
            new(CorrelationId, [], [module, otherModule], [contributed]),
            CancellationToken.None);

        Assert.Empty(contribution.Requests);
        var refusal = Assert.Single(contribution.Refusals);
        Assert.Equal("Acme.OtherModule", refusal.PackageId);
        Assert.Equal("capability-conflict", refusal.Stage);
    }

    [Fact]
    public async Task ContributeAsync_WhenCancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.ContributeAsync(Context([DeclaringModule()]), cancellation.Token));
    }

    private void Select(params string[] options) =>
        _capabilityOptions.Selections[Capability] = new() { Options = options };

    private ResolvedPackage DeclaringModule(string packageId = "Acme.Module") =>
        _packages.Install(
            packageId,
            metadata: InstalledPackageStore.CapabilityMetadata(
                Capability,
                ("PostgreSql", EnginePackageId, "[10.0.0]"),
                ("Sqlite", OtherEnginePackageId, "[10.0.10]")));

    private static PackageRequest ExplicitRoot(string packageId, string versionRange = "[1.0.0]") =>
        new(packageId, versionRange, null, PackageUpdatePolicy.Exact, "test-source");

    private static DesiredStateContributionContext Context(
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        params PackageRequest[] desiredRequests) =>
        new(CorrelationId, desiredRequests, resolvedPackages, []);

    private Task<DesiredStateContribution> ContributeAsync(
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        params PackageRequest[] desiredRequests) =>
        _sut.ContributeAsync(Context(resolvedPackages, desiredRequests), CancellationToken.None);
}
