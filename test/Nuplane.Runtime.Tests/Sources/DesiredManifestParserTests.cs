using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Sources;

/// <summary>
/// Unit tests for <see cref="DesiredManifestReader"/> covering schema parsing,
/// exact-version validation, and error reporting.
/// </summary>
public sealed class DesiredManifestParserTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-manifest-{Guid.NewGuid():N}");
    private readonly DesiredManifestReader _reader = new();
    private const string CorrelationId = "test-correlation-001";

    public DesiredManifestParserTests()
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
    public async Task ReadAsync_ValidManifest_ReturnsSucceeded()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "PackageA", Version = "1.0.0", SourceHint = (string?)null },
                new { Id = "PackageB", Version = "2.3.1", SourceHint = (string?)"feed-x" }
            }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestSucceeded, result.ReasonCode);
        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Manifest!.Packages.Count);
    }

    [Fact]
    public async Task ReadAsync_ValidManifest_PreservesSchemaVersionAndTimestamp()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = timestamp,
            Packages = new[] { new { Id = "Pkg", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal("1.0", result.Manifest!.SchemaVersion);
    }

    [Fact]
    public async Task ReadAsync_ExactVersion_ReturnsSucceeded()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "3.2.1-beta.5" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Equal("3.2.1-beta.5", result.Manifest!.Packages[0].Version);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("[1.0.0, 2.0.0)")]
    [InlineData("(1.0.0, )")]
    public async Task ReadAsync_VersionRange_ReturnsInvalid(string versionRange)
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = versionRange } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestInvalid, result.ReasonCode);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task ReadAsync_DuplicatePackageIds_ReturnsInvalid()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Duplicate", Version = "1.0.0" },
                new { Id = "Duplicate", Version = "2.0.0" }
            }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task ReadAsync_EmptyPackageId_ReturnsInvalid()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_EmptyVersion_ReturnsInvalid()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNotFound()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.NotFound, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestNotFound, result.ReasonCode);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task ReadAsync_InvalidJson_ReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "invalid.json");
        await File.WriteAllTextAsync(path, "not valid json {{{");

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task ReadAsync_MissingSchemaVersion_ReturnsInvalid()
    {
        var path = WriteManifest(new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_NullPackages_ReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "null-pkgs.json");
        await File.WriteAllTextAsync(path, """{"SchemaVersion":"1.0","GeneratedAtUtc":"2025-01-01T00:00:00Z"}""");

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_EmptyPackages_ReturnsSucceeded()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = Array.Empty<object>()
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Empty(result.Manifest!.Packages);
    }

    [Fact]
    public async Task ReadAsync_PreservesSourceHintAndSha512()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[]
            {
                new { Id = "Pkg", Version = "1.0.0", SourceHint = "my-feed", Sha512 = "abc123" }
            }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal("my-feed", result.Manifest!.Packages[0].SourceHint);
        Assert.Equal("abc123", result.Manifest.Packages[0].Sha512);
    }

    [Fact]
    public async Task ReadAsync_SetsCorrelationIdAndSourceId()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = Array.Empty<object>()
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(CorrelationId, result.CorrelationId);
        Assert.Equal(path, result.SourceId);
    }

    [Fact]
    public async Task ReadAsync_NullFilePath_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _reader.ReadAsync(null!, CorrelationId, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_NullCorrelationId_ThrowsArgumentException()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _reader.ReadAsync("/some/path", null!, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_CaseInsensitivePropertyNames_ParsesCorrectly()
    {
        var path = Path.Combine(_tempDir, "case.json");
        await File.WriteAllTextAsync(path, """
        {
            "schemaversion": "1.0",
            "generatedatutc": "2025-01-01T00:00:00Z",
            "packages": [
                { "id": "pkg-a", "version": "1.0.0" }
            ]
        }
        """);

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
        Assert.Single(result.Manifest!.Packages);
    }

    [Fact]
    public async Task ReadAsync_MatchingExpectedSchemaVersion_ReturnsSucceeded()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None, expectedSchemaVersion: "1.0");

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MismatchedExpectedSchemaVersion_ReturnsInvalid()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None, expectedSchemaVersion: "2.0");

        Assert.Equal(ManifestReadStatus.Invalid, result.Status);
        Assert.Equal(ConvergenceReasonCodes.ManifestInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task ReadAsync_NullExpectedSchemaVersion_DoesNotEnforceVersion()
    {
        var path = WriteManifest(new
        {
            SchemaVersion = "9.9",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Pkg", Version = "1.0.0" } }
        });

        var result = await _reader.ReadAsync(path, CorrelationId, CancellationToken.None, expectedSchemaVersion: null);

        Assert.Equal(ManifestReadStatus.Succeeded, result.Status);
    }
}
