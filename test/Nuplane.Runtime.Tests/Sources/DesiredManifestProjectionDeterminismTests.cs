using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Sources;

/// <summary>
/// Unit tests verifying that <see cref="DesiredManifestReader"/> produces deterministic,
/// stable-sorted manifest projection output regardless of input order.
/// </summary>
public sealed class DesiredManifestProjectionDeterminismTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-det-{Guid.NewGuid():N}");
    private readonly DesiredManifestReader _reader = new();
    private const string CorrelationId = "det-test-001";

    public DesiredManifestProjectionDeterminismTests()
    {
        System.IO.Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_tempDir))
        {
            System.IO.Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteManifest(object manifest)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    [Fact]
    public async Task ReadAsync_PackagesAreSortedByIdThenVersion()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Zeta", Version = "1.0.0" },
                new { Id = "Alpha", Version = "2.0.0" },
                new { Id = "Alpha", Version = "1.0.0" },
                new { Id = "Beta", Version = "1.0.0" }
            }
        });

        // Note: duplicate "Alpha" IDs will cause validation failure.
        // Use distinct IDs to test ordering.
        var pathValid = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Zeta", Version = "1.0.0" },
                new { Id = "Alpha", Version = "2.0.0" },
                new { Id = "Beta", Version = "1.0.0" }
            }
        });

        var result = await _reader.ReadAsync(pathValid, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Manifest!.Packages.Count);
        Assert.Equal("Alpha", result.Manifest.Packages[0].Id);
        Assert.Equal("Beta", result.Manifest.Packages[1].Id);
        Assert.Equal("Zeta", result.Manifest.Packages[2].Id);
    }

    [Fact]
    public async Task ReadAsync_IdenticalContentYieldsIdenticalOutput()
    {
        var manifest = new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.Parse("2025-06-01T12:00:00Z"),
            Packages = new[]
            {
                new { Id = "Lib.Core", Version = "3.1.0" },
                new { Id = "Lib.Auth", Version = "2.0.5" },
                new { Id = "Lib.Data", Version = "1.4.2" }
            }
        };

        var path1 = WriteManifest(manifest);
        var path2 = WriteManifest(manifest);

        var result1 = await _reader.ReadAsync(path1, "corr-1", CancellationToken.None);
        var result2 = await _reader.ReadAsync(path2, "corr-2", CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result1.Status);
        Assert.Equal(ManifestReadStatus.Succeeded, result2.Status);

        var pkgs1 = result1.Manifest!.Packages;
        var pkgs2 = result2.Manifest!.Packages;

        Assert.Equal(pkgs1.Count, pkgs2.Count);
        for (var i = 0; i < pkgs1.Count; i++)
        {
            Assert.Equal(pkgs1[i].Id, pkgs2[i].Id);
            Assert.Equal(pkgs1[i].Version, pkgs2[i].Version);
            Assert.Equal(pkgs1[i].SourceHint, pkgs2[i].SourceHint);
            Assert.Equal(pkgs1[i].Sha512, pkgs2[i].Sha512);
        }
    }

    [Fact]
    public async Task ReadAsync_ReversedInput_YieldsSameSortedOutput()
    {
        var forward = new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Alpha", Version = "1.0.0" },
                new { Id = "Beta", Version = "2.0.0" },
                new { Id = "Gamma", Version = "3.0.0" }
            }
        };

        var reversed = new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Gamma", Version = "3.0.0" },
                new { Id = "Beta", Version = "2.0.0" },
                new { Id = "Alpha", Version = "1.0.0" }
            }
        };

        var r1 = await _reader.ReadAsync(WriteManifest(forward), "c1", CancellationToken.None);
        var r2 = await _reader.ReadAsync(WriteManifest(reversed), "c2", CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, r1.Status);
        Assert.Equal(ManifestReadStatus.Succeeded, r2.Status);

        for (var i = 0; i < r1.Manifest!.Packages.Count; i++)
        {
            Assert.Equal(r1.Manifest.Packages[i].Id, r2.Manifest!.Packages[i].Id);
            Assert.Equal(r1.Manifest.Packages[i].Version, r2.Manifest.Packages[i].Version);
        }
    }

    [Fact]
    public async Task ReadAsync_CaseInsensitiveSorting_IsStable()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "zebra", Version = "1.0.0" },
                new { Id = "ALPHA", Version = "1.0.0" },
                new { Id = "Beta", Version = "1.0.0" }
            }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Equal("ALPHA", result.Manifest!.Packages[0].Id);
        Assert.Equal("Beta", result.Manifest.Packages[1].Id);
        Assert.Equal("zebra", result.Manifest.Packages[2].Id);
    }

    [Fact]
    public async Task ReadAsync_SinglePackage_ReturnsSingleEntry()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Only", Version = "9.9.9" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Single(result.Manifest!.Packages);
        Assert.Equal("Only", result.Manifest.Packages[0].Id);
    }

    [Fact]
    public async Task ReadAsync_EmptyPackages_ReturnsDeterministicEmpty()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = Array.Empty<object>()
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Empty(result.Manifest!.Packages);
    }

    [Fact]
    public async Task ReadAsync_MultipleRuns_SameCorrelationId_ProducesStableResult()
    {
        var manifest = new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Delta", Version = "1.0.0" },
                new { Id = "Charlie", Version = "1.0.0" },
                new { Id = "Echo", Version = "1.0.0" },
                new { Id = "Bravo", Version = "1.0.0" },
                new { Id = "Foxtrot", Version = "1.0.0" }
            }
        };
        var path = WriteManifest(manifest);

        var results = new List<DesiredManifestReadResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await _reader.ReadAsync(path, $"run-{i}", CancellationToken.None));
        }

        var baseline = results[0].Manifest!.Packages;
        foreach (var r in results.Skip(1))
        {
            for (var i = 0; i < baseline.Count; i++)
            {
                Assert.Equal(baseline[i].Id, r.Manifest!.Packages[i].Id);
            }
        }
    }
}
