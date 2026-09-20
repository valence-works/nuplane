using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Integration tests verifying that manifest-driven convergence produces
/// identical outcomes across independent replicas and that manifest updates
/// drive deterministic eventual convergence.
/// </summary>
public sealed class ManifestConvergenceIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-conv-{Guid.NewGuid():N}");

    public ManifestConvergenceIntegrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteManifestFile(object manifest)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static ReconciliationService CreateService(IDesiredPackageSource source)
    {
        return ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: new NuGetPackageResolver());
    }

    [Fact]
    public async Task TwoReplicas_SameManifest_ConvergeToIdenticalActiveSet()
    {
        var manifestPath = WriteManifestFile(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Lib.Core", Version = "1.0.0" },
                new { Id = "Lib.Auth", Version = "2.0.0" },
                new { Id = "Lib.Data", Version = "3.1.0" }
            }
        });

        var options = new ConvergenceOptions { Manifest = { Enabled = true, Path = manifestPath } };

        var source1 = new DesiredManifestPackageSource(new(), options);
        var source2 = new DesiredManifestPackageSource(new(), options);

        var service1 = CreateService(source1);
        var service2 = CreateService(source2);

        var result1 = await service1.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        var result2 = await service2.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result1.IsDegraded);
        Assert.False(result2.IsDegraded);
        Assert.Equal(result1.ChangeSet.Added.Count, result2.ChangeSet.Added.Count);

        var ids1 = result1.ChangeSet.Added.Select(p => p.Id).OrderBy(id => id).ToList();
        var ids2 = result2.ChangeSet.Added.Select(p => p.Id).OrderBy(id => id).ToList();
        Assert.Equal(ids1, ids2);
    }

    [Fact]
    public async Task ManifestUpdate_DrivesConvergence_ToNewState()
    {
        var manifestPath = Path.Combine(_tempDir, "evolving.json");
        var options = new ConvergenceOptions { Manifest = { Enabled = true, Path = manifestPath } };

        // Initial manifest
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Pkg", Version = "1.0.0" }
            }
        }));

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var first = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        Assert.Single(first.ChangeSet.Added);
        Assert.Equal("1.0.0", first.ChangeSet.Added[0].Version);

        // Update manifest to new version
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Pkg", Version = "2.0.0" }
            }
        }));

        var second = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Single(second.ChangeSet.Updated);
        Assert.Equal("Pkg", second.ChangeSet.Updated[0].Id);
        Assert.Equal("2.0.0", second.ChangeSet.Updated[0].Version);
    }

    [Fact]
    public async Task ManifestPackageRemoved_DrivesRemovalOnNextCycle()
    {
        var manifestPath = Path.Combine(_tempDir, "removal.json");
        var options = new ConvergenceOptions { Manifest = { Enabled = true, Path = manifestPath } };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "KeepMe", Version = "1.0.0" },
                new { Id = "RemoveMe", Version = "1.0.0" }
            }
        }));

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        // Remove one package from manifest
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "KeepMe", Version = "1.0.0" }
            }
        }));

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Single(result.ChangeSet.Removed);
        Assert.Equal("RemoveMe", result.ChangeSet.Removed[0]);
    }

    [Fact]
    public async Task ManifestDisabledByDefault_ReconciliationCycle_PersistsNoManifestSourceSnapshot()
    {
        // DesiredManifestPackageSource is always registered on the shared runtime path, but a
        // host that never enables manifest convergence must see no observable effect from it:
        // no snapshot entry should be written to store state for a disabled manifest source.
        var options = new ConvergenceOptions();
        var source = new DesiredManifestPackageSource(new(), options);
        var store = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            storeRegistry: store,
            packageResolver: new NuGetPackageResolver());

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result.IsDegraded);
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.DoesNotContain(state.LastSuccessfulSourceSnapshots.Keys, key => key.Contains(nameof(DesiredManifestPackageSource), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestEnabled_ReconciliationCycle_ManifestPackagesReachDesiredRequestsAndArePersisted()
    {
        var manifestPath = WriteManifestFile(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Lib.Core", Version = "1.0.0" }
            }
        });

        var options = new ConvergenceOptions { Manifest = { Enabled = true, Path = manifestPath } };
        var source = new DesiredManifestPackageSource(new(), options);
        var store = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            storeRegistry: store,
            packageResolver: new NuGetPackageResolver());

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        var added = Assert.Single(result.ChangeSet.Added);
        Assert.Equal("Lib.Core", added.Id);

        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.Contains(state.LastSuccessfulSourceSnapshots.Keys, key => key.Contains(nameof(DesiredManifestPackageSource), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddNuplane_FromConfiguration_ManifestEnabled_ReconciliationCycleIncludesManifestPackage()
    {
        var manifestPath = WriteManifestFile(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Lib.Core", Version = "1.0.0" }
            }
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Convergence:Manifest:Enabled"] = "true",
                ["Nuplane:Convergence:Manifest:Path"] = manifestPath,
                ["Nuplane:Setup:UseInMemoryStore"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"));

        var added = await ResolveAndTriggerCycleAsync(services);
        Assert.Equal("Lib.Core", added.Id);
    }

    [Fact]
    public async Task AddNuplane_BuilderOnly_ManifestEnabledViaCodeConfigure_ReconciliationCycleIncludesManifestPackage()
    {
        var manifestPath = WriteManifestFile(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Lib.Core", Version = "1.0.0" }
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();

        // The manifest is enabled purely through code, with no IConfiguration involved, exercising
        // the builder-only AddNuplane overload.
        services.Configure<ConvergenceOptions>(options =>
        {
            options.Manifest.Enabled = true;
            options.Manifest.Path = manifestPath;
        });
        services.AddNuplane(nuplane => nuplane.UseInMemoryStore());

        var added = await ResolveAndTriggerCycleAsync(services);
        Assert.Equal("Lib.Core", added.Id);
    }

    /// <summary>
    /// Resolves a DI-composed <see cref="ReconciliationService"/> from <paramref name="services"/>
    /// and runs one reconciliation cycle, returning the single package the cycle added. Package
    /// resolution is swapped for the deterministic, offline <see cref="NuGetPackageResolver"/> (the
    /// last registration wins over the <c>MultiFeedPackageResolver</c> that <c>AddNuplane</c>
    /// registers by default) so the cycle needs no configured feed and makes no network calls, and
    /// the store is kept in memory so the cycle never sees state left behind by another test run.
    /// </summary>
    private static async Task<ResolvedPackage> ResolveAndTriggerCycleAsync(ServiceCollection services)
    {
        services.AddSingleton<IPackageResolver>(new NuGetPackageResolver());

        await using var provider = services.BuildServiceProvider();
        var reconciliation = provider.GetRequiredService<ReconciliationService>();

        var result = await reconciliation.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        return Assert.Single(result.ChangeSet.Added);
    }
}
