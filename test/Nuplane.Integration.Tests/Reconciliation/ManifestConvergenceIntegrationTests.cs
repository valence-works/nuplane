using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;

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
            sourceTrustOptions: new() { RejectUnallowlistedPackages = false },
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

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
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
}
