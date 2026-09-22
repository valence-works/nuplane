using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources.Directory.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Restore;

/// <summary>
/// A selected capability from end to end: one real reconciliation cycle, run through the host-free
/// restore entry point, over two local directory feeds and temporary directories. Nothing here
/// touches a network.
/// <para>
/// The module package lives in a <c>DesiredAndCache</c> feed, so it is an explicit desired root. The
/// option packages live in a second feed whose role is <c>Cache</c>: it resolves packages but
/// produces no desired roots, which is exactly the situation a capability exists for — the engine is
/// available, and nothing in the host's configuration names it.
/// </para>
/// </summary>
public sealed class CapabilityContributionCycleTests : IDisposable
{
    private const string ModuleFeedName = "modules";
    private const string EngineFeedName = "engines";
    private const string Capability = "ef-provider";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuplane-capability-cycle", Guid.NewGuid().ToString("N"));
    private readonly string _moduleFeedDirectory;
    private readonly string _engineFeedDirectory;
    private readonly string _installRoot;
    private readonly string _stateFilePath;
    private readonly string _lockFilePath;

    public CapabilityContributionCycleTests()
    {
        _moduleFeedDirectory = Path.Combine(_root, "modules");
        _engineFeedDirectory = Path.Combine(_root, "engines");
        _installRoot = Path.Combine(_root, "packages");
        _stateFilePath = Path.Combine(_root, "state", "store-state.json");
        _lockFilePath = Path.Combine(_root, "nuplane.lock.json");
        Directory.CreateDirectory(_moduleFeedDirectory);
        Directory.CreateDirectory(_engineFeedDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_WithASelectedOption_InstallsItAndTheStoreRecordsItAsARoot()
    {
        var (engine, other) = WriteEngines();
        var module = WriteModule(engine, other);

        var result = await RestoreAsync(Select("PostgreSql"));

        Assert.False(result.IsDegraded);
        Assert.Empty(result.FailedPackages);
        Assert.True(File.Exists(Path.Combine(engine.InstallDirectory(_installRoot, EngineFeedName), ".nuplane-ready")));

        var active = Assert.Single(result.ActivePackages, package => package.PackageId == engine.PackageId);
        Assert.Equal(ActivePackageRole.Root, active.PackageRole);
        Assert.Equal($"capability:{Capability}=PostgreSql", active.SourceName);
        Assert.Contains(engine.PackageId, active.RootPackageIds);
        Assert.True(active.Discoverable);

        // The same thing a host — or Elsa's worker — reads back from the store offline.
        var readBack = await NuplaneStore.ReadActivePackagesAsync(result.StateFilePath);
        Assert.Equal(ActivePackageRole.Root, Assert.Single(readBack, package => package.PackageId == engine.PackageId).PackageRole);
        Assert.DoesNotContain(other.PackageId, readBack.Select(static package => package.PackageId));
        Assert.Equal(
            [engine.PackageId, module.PackageId],
            readBack.Select(static package => package.PackageId).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreAsync_WithNoSelection_DegradesTheCycleAndInstallsNoOption()
    {
        var (engine, other) = WriteEngines();
        var module = WriteModule(engine, other);

        var result = await RestoreAsync();

        Assert.True(result.IsDegraded);
        Assert.Equal(module.PackageId, Assert.Single(result.FailedPackages));
        Assert.Empty(result.ActivePackages);
        Assert.False(Directory.Exists(engine.InstallDirectory(_installRoot, EngineFeedName)));
        Assert.False(Directory.Exists(other.InstallDirectory(_installRoot, EngineFeedName)));

        var failure = await LastFailureAsync(module.PackageId);
        Assert.Equal("capability-unselected", failure.Stage);
        Assert.Contains("PostgreSql", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"Nuplane:Capabilities:{Capability}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAsync_WhenTheHostNamesTheOptionPackageItself_ContributesNothingAndKeepsItsSourceName()
    {
        // The engine written into the module feed is an ordinary desired root, so the capability is
        // satisfied without a selection and without a contribution.
        var engine = HostFreeRestoreTestSupport.WriteNupkg(_moduleFeedDirectory, prefix: "Engine.PostgreSql");
        var other = HostFreeRestoreTestSupport.WriteNupkg(_engineFeedDirectory, prefix: "Engine.Sqlite");
        var module = WriteModule(engine, other);

        var result = await RestoreAsync();

        Assert.False(result.IsDegraded);
        Assert.Empty(result.FailedPackages);
        Assert.Equal(
            [engine.PackageId, module.PackageId],
            result.ActivePackages.Select(static package => package.PackageId).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(ModuleFeedName, Assert.Single(result.ActivePackages, package => package.PackageId == engine.PackageId).SourceName);
    }

    [Fact]
    public async Task RestoreAsync_WithTheSameSelectionTwice_IsIdempotent()
    {
        var (engine, other) = WriteEngines();
        WriteModule(engine, other);
        var selection = Select("PostgreSql");

        var first = await RestoreAsync(selection);
        var second = await RestoreAsync(selection);

        Assert.False(second.IsDegraded);
        Assert.Equal(
            first.ActivePackages.Select(static package => (package.PackageId, package.Version, package.PackageRole, package.SourceName)),
            second.ActivePackages.Select(static package => (package.PackageId, package.Version, package.PackageRole, package.SourceName)));
        Assert.Equal(GraphId(first, engine.PackageId), GraphId(second, engine.PackageId));
    }

    [Fact]
    public async Task RestoreAsync_WhenTheSelectionChanges_ReconcilesTheOldOptionOutAndTheNewOneInWithANewGraph()
    {
        var (engine, other) = WriteEngines();
        WriteModule(engine, other);

        var first = await RestoreAsync(Select("PostgreSql"));
        var second = await RestoreAsync(Select("Sqlite"));

        Assert.False(second.IsDegraded);
        Assert.Contains(engine.PackageId, first.ActivePackages.Select(static package => package.PackageId));
        Assert.DoesNotContain(engine.PackageId, second.ActivePackages.Select(static package => package.PackageId));
        var newOption = Assert.Single(second.ActivePackages, package => package.PackageId == other.PackageId);
        Assert.Equal(ActivePackageRole.Root, newOption.PackageRole);
        Assert.Equal($"capability:{Capability}=Sqlite", newOption.SourceName);
        Assert.NotEqual(GraphId(first, engine.PackageId), GraphId(second, other.PackageId));
    }

    [Fact]
    public async Task RestoreAsync_WithAStrictLockFileThatDoesNotLockTheContribution_RefusesTheContribution()
    {
        var (engine, other) = WriteEngines();
        var module = WriteModule(engine, other);
        WriteLockFile(module);

        var result = await RestoreAsync(
            Select("PostgreSql"),
            ("Nuplane:LockFile:Mode", "Strict"),
            ("Nuplane:LockFile:RequireEntryInStrictMode", "true"));

        Assert.True(result.IsDegraded);
        Assert.Contains(engine.PackageId, result.FailedPackages);
        Assert.DoesNotContain(engine.PackageId, result.ActivePackages.Select(static package => package.PackageId));

        // Refused by the lock gate, not by anything capability-specific: a contributed root is
        // evaluated against the lock file exactly like a root the host named.
        var failure = await LastFailureAsync(engine.PackageId);
        Assert.Equal("lock", failure.Stage);
        Assert.Equal("strict-missing-entry", failure.Message);
    }

    [Fact]
    public async Task RestoreAsync_TheDryRunPlanOfTheCycleEqualsTheChangeSetItApplied()
    {
        var (engine, other) = WriteEngines();
        var module = WriteModule(engine, other);
        var spy = new CyclePlanSpy();

        var result = await RestoreAsync(Select("PostgreSql"), configureBuilder: spy.Register);

        Assert.False(result.IsDegraded);
        var plan = Assert.Single(spy.Plans);
        var changeSet = Assert.Single(spy.ChangeSets);
        Assert.Equal(
            plan.ChangeSet.Added.Select(static package => (package.Id, package.Version)).Order(),
            changeSet.Added.Select(static package => (package.Id, package.Version)).Order());
        Assert.Equal(plan.ChangeSet.Updated.Count, changeSet.Updated.Count);
        Assert.Equal(plan.ChangeSet.Removed.Order(), changeSet.Removed.Order());

        // The point of the parity: a dry run already includes what the capability contributed.
        Assert.Equal(
            [engine.PackageId, module.PackageId],
            plan.ChangeSet.Added.Select(static package => package.Id).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreAsync_WithRequirePinnedVersionsAndAPinnedContribution_RestoresItAsARoot()
    {
        var (engine, other) = WriteEngines();
        WriteModule(engine, other);

        var result = await RestoreAsync(Select("PostgreSql"), pinnedOnly: true);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Empty(result.UnpinnedRequests);
        var active = Assert.Single(result.ActivePackages, package => package.PackageId == engine.PackageId);
        Assert.Equal(ActivePackageRole.Root, active.PackageRole);
        Assert.Equal(
            ActivePackageRole.Root,
            Assert.Single(await NuplaneStore.ReadActivePackagesAsync(result.StateFilePath), package => package.PackageId == engine.PackageId).PackageRole);
    }

    [Fact]
    public async Task RestoreAsync_WithRequirePinnedVersionsAndARangedContribution_RefusesItBeforeDownloadingIt()
    {
        var (engine, other) = WriteEngines();
        var module = WriteModule(engine, other, optionVersionRange: "[1.0.0,2.0.0)");

        var result = await RestoreAsync(Select("PostgreSql"), pinnedOnly: true);

        // Not a skip: the roots were acquired. Degraded, with the declaring package failed and the
        // contribution reported — and the option package itself never fetched.
        Assert.False(result.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.None, result.SkipReason);
        Assert.True(result.IsDegraded);
        Assert.Equal(module.PackageId, Assert.Single(result.FailedPackages));
        Assert.False(Directory.Exists(engine.InstallDirectory(_installRoot, EngineFeedName)));

        var unpinned = Assert.Single(result.UnpinnedRequests);
        Assert.Equal(engine.PackageId, unpinned.PackageId);
        Assert.Equal("[1.0.0,2.0.0)", unpinned.VersionRange);
        Assert.Equal($"capability:{Capability}=PostgreSql", unpinned.SourceName);
        Assert.False(unpinned.IsPinned);
        Assert.Null(unpinned.PinnedVersion);
    }

    [Fact]
    public async Task DescribeDesiredAsync_ReportsTheSelectionButNotTheRootItWouldContribute()
    {
        var (engine, other) = WriteEngines();
        WriteModule(engine, other);

        var description = await NuplaneRestore.DescribeDesiredAsync(Configure(Select("PostgreSql")), Options());

        Assert.Equal("PostgreSql", Assert.Single(description.CapabilitySelections[Capability].Options));
        Assert.DoesNotContain(engine.PackageId, description.Requests.Select(static request => request.PackageId));
    }

    private static (string Key, string? Value) Select(string option) =>
        ($"Nuplane:Capabilities:{Capability}", option);

    /// <summary>The failure the cycle recorded in the store for <paramref name="packageId"/>.</summary>
    private async Task<FailureRecord> LastFailureAsync(string packageId)
    {
        var state = await new StoreStateSerializer().LoadAsync(_stateFilePath, CancellationToken.None);
        return Assert.Contains(packageId, state.LastFailureById);
    }

    private static string GraphId(NuplaneRestoreResult result, string packageId) =>
        Assert.Single(result.ActivePackages, package => package.PackageId == packageId).GraphId;

    /// <summary>The two option packages, in the cache-role feed that names none of them as desired.</summary>
    private (PackageFixture PostgreSql, PackageFixture Sqlite) WriteEngines() =>
        (HostFreeRestoreTestSupport.WriteNupkg(_engineFeedDirectory, prefix: "Engine.PostgreSql"),
            HostFreeRestoreTestSupport.WriteNupkg(_engineFeedDirectory, prefix: "Engine.Sqlite"));

    /// <summary>The declaring module, in the desired feed, shipping a schema-2 <c>nuplane.json</c>.</summary>
    private PackageFixture WriteModule(
        PackageFixture postgreSql,
        PackageFixture sqlite,
        string optionVersionRange = "[1.0.0]") =>
        HostFreeRestoreTestSupport.WriteNupkg(
            _moduleFeedDirectory,
            prefix: "Module",
            metadata: $$"""
            {
              "schemaVersion": 2,
              "capabilities": [
                {
                  "name": "{{Capability}}",
                  "description": "The engine this module binds at run time.",
                  "options": [
                    { "name": "PostgreSql", "packageId": "{{postgreSql.PackageId}}", "version": "{{optionVersionRange}}" },
                    { "name": "Sqlite", "packageId": "{{sqlite.PackageId}}", "version": "[1.0.0]" }
                  ]
                }
              ]
            }
            """);

    private void WriteLockFile(PackageFixture package) =>
        File.WriteAllText(
            _lockFilePath,
            $$"""
            {
              "schemaVersion": "1.0",
              "generatedAt": "{{DateTimeOffset.UtcNow:O}}",
              "packages": [
                {
                  "id": "{{package.PackageId}}",
                  "version": "{{package.Version}}",
                  "feed": "{{ModuleFeedName}}",
                  "hash": "",
                  "timestamp": "{{DateTimeOffset.UtcNow:O}}"
                }
              ]
            }
            """);

    private Task<NuplaneRestoreResult> RestoreAsync(
        params (string Key, string? Value)[] settings) =>
        NuplaneRestore.RestoreAsync(Configure(settings), Options());

    private Task<NuplaneRestoreResult> RestoreAsync(
        (string Key, string? Value) selection,
        bool pinnedOnly = false,
        Action<NuplaneBuilder>? configureBuilder = null,
        params (string Key, string? Value)[] settings) =>
        NuplaneRestore.RestoreAsync(
            Configure([selection, .. settings]),
            Options(options =>
            {
                options.RequirePinnedVersions = pinnedOnly;
                if (configureBuilder is not null)
                {
                    options.ConfigureBuilder = (builder, configuration) =>
                    {
                        builder.AddDirectoryFeedsFromConfiguration(configuration);
                        configureBuilder(builder);
                    };
                }
            }));

    private IConfigurationRoot Configure(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Nuplane:Setup:Feeds:{ModuleFeedName}:DirectoryPath"] = _moduleFeedDirectory,
                    [$"Nuplane:Setup:Feeds:{ModuleFeedName}:IncludeAll"] = "true",
                    [$"Nuplane:Setup:Feeds:{ModuleFeedName}:Directory:Watch"] = "false",
                    [$"Nuplane:Setup:Feeds:{EngineFeedName}:DirectoryPath"] = _engineFeedDirectory,
                    [$"Nuplane:Setup:Feeds:{EngineFeedName}:IncludeAll"] = "true",
                    [$"Nuplane:Setup:Feeds:{EngineFeedName}:Directory:Watch"] = "false",
                    // Resolves packages, produces no desired roots: the option packages are
                    // available but nothing in the configuration asks for them.
                    [$"Nuplane:Setup:Feeds:{EngineFeedName}:Directory:Role"] = "Cache"
                }
                .Concat(settings.Select(static setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
                .ToDictionary(static setting => setting.Key, static setting => setting.Value))
            .Build();

    private NuplaneRestoreOptions Options(Action<NuplaneRestoreOptions>? configure = null)
    {
        var options = new NuplaneRestoreOptions
        {
            InstallRoot = _installRoot,
            StateFilePath = _stateFilePath,
            LockFilePath = _lockFilePath,
            ConfigureBuilder = static (builder, configuration) => builder.AddDirectoryFeedsFromConfiguration(configuration)
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Captures the dry-run plan the cycle built and the change set it then applied, so the two can
    /// be compared. The planner is decorated rather than replaced, so the plan under test is the one
    /// the real planner produces from the cycle's own resolved set.
    /// </summary>
    private sealed class CyclePlanSpy
    {
        public List<DryRunPlan> Plans { get; } = [];

        public List<PackageChangeSet> ChangeSets { get; } = [];

        public void Register(NuplaneBuilder builder)
        {
            builder.Services.AddSingleton<IDryRunPlanner>(sp => new RecordingDryRunPlanner(
                new DryRunPlanner(sp.GetRequiredService<IDesiredActualDiffEngine>()),
                Plans));
            builder.Services.AddSingleton<INuplaneObserver>(new RecordingObserver(ChangeSets));
        }

        private sealed class RecordingDryRunPlanner(IDryRunPlanner inner, List<DryRunPlan> plans) : IDryRunPlanner
        {
            public async Task<DryRunPlan> BuildPlanAsync(
                IReadOnlyCollection<ResolvedPackage> desired,
                IReadOnlyDictionary<string, string> activeVersions,
                string correlationId,
                CancellationToken cancellationToken)
            {
                var plan = await inner.BuildPlanAsync(desired, activeVersions, correlationId, cancellationToken);
                plans.Add(plan);
                return plan;
            }
        }

        private sealed class RecordingObserver(List<PackageChangeSet> changeSets) : INuplaneObserver
        {
            public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

            public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
            {
                changeSets.Add(changeSet);
                return Task.CompletedTask;
            }

            public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct) => Task.CompletedTask;
        }
    }
}
