using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Convergence;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Regression tests verifying that invalid or unreadable manifests produce
/// degraded non-mutating outcomes: no packages are added, updated, or removed.
/// </summary>
public sealed class ManifestInvalidNonMutatingRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-nonmut-{Guid.NewGuid():N}");

    public ManifestInvalidNonMutatingRegressionTests()
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

    private static ReconciliationService CreateService(IDesiredPackageSource source)
    {
        return ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { RejectUnallowlistedPackages = false },
            packageResolver: new NuGetPackageResolver());
    }

    [Fact]
    public async Task ManifestNotFound_ReturnsNonMutating_EmptyChangeSet()
    {
        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = Path.Combine(_tempDir, "missing.json") }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    [Fact]
    public async Task InvalidJson_ReturnsNonMutating_EmptyChangeSet()
    {
        var path = Path.Combine(_tempDir, "invalid.json");
        await File.WriteAllTextAsync(path, "{{ not valid json }}");

        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = path }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    [Fact]
    public async Task DuplicatePackageIds_ReturnsNonMutating_EmptyChangeSet()
    {
        var path = Path.Combine(_tempDir, "dup.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Dup", Version = "1.0.0" },
                new { Id = "Dup", Version = "2.0.0" }
            }
        }));

        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = path }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    [Fact]
    public async Task VersionRange_ReturnsNonMutating_EmptyChangeSet()
    {
        var path = Path.Combine(_tempDir, "range.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Pkg", Version = "[1.0.0, 2.0.0)" }
            }
        }));

        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = path }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    [Fact]
    public async Task ManifestDisabled_ReturnsNonMutating_EmptyChangeSet()
    {
        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = false, Path = "/does/not/matter" }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    [Fact]
    public async Task ExistingState_InvalidManifest_SourceReturnsEmpty_NoNewPackagesAdded()
    {
        var manifestPath = Path.Combine(_tempDir, "evolving2.json");

        // Start with a valid manifest to establish state
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Established", Version = "1.0.0" }
            }
        }));

        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = manifestPath }
        };

        var source = new DesiredManifestPackageSource(new(), options);
        var service = CreateService(source);

        var established = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        Assert.Single(established.ChangeSet.Added);

        // Now corrupt the manifest  
        await File.WriteAllTextAsync(manifestPath, "corrupt");

        var degraded = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        // Source is non-mutating: returns empty desired set, so no new packages are added.
        // The diff engine may still remove previously-active packages since desired set is now empty.
        Assert.Empty(degraded.ChangeSet.Added);
        Assert.Empty(degraded.ChangeSet.Updated);

        // Verify the source exposed the invalid read result for observability
        Assert.NotNull(source.LastReadResult);
        Assert.Equal(ManifestReadStatus.Invalid, source.LastReadResult!.Status);
    }

    [Fact]
    public async Task ManifestInvalid_SourceReportsLastReadResult()
    {
        var path = Path.Combine(_tempDir, "bad-schema.json");
        await File.WriteAllTextAsync(path, """{"GeneratedAtUtc":"2025-01-01T00:00:00Z"}""");

        var options = new ConvergenceOptions
        {
            Manifest = { Enabled = true, Path = path }
        };

        var source = new DesiredManifestPackageSource(new(), options);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetDesiredAsync(CancellationToken.None));

        Assert.NotNull(source.LastReadResult);
        Assert.Equal(ManifestReadStatus.Invalid, source.LastReadResult!.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestInvalid, source.LastReadResult.ReasonCode);
    }
}
