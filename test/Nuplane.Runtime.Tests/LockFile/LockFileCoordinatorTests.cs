using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.LockFile;

public sealed class LockFileCoordinatorTests : IDisposable
{
    private readonly string _lockFilePath = Path.GetTempFileName();

    public void Dispose()
    {
        if (File.Exists(_lockFilePath))
        {
            File.Delete(_lockFilePath);
        }
    }

    [Fact]
    public async Task EvaluateAsync_LockFileAbsent_AllResolutionsPermitted()
    {
        File.Delete(_lockFilePath); // ensure absent
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);
        var pkg = Pkg("alpha", "1.0.0", "feed-a");

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("enforce-no-entry", result.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_LockFilePresent_EnforceModeOverridesVersion()
    {
        await WriteLockFileAsync(_lockFilePath, [new("alpha", "2.0.0", "feed-a", "abc123hash", DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);
        var pkg = Pkg("alpha", "1.0.0", "feed-a");

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("enforced", result.ReasonCode);
        Assert.Equal("2.0.0", result.EffectivePackage!.Version);
    }

    [Fact]
    public async Task EvaluateAsync_GenerateMode_PassesThroughWithoutEnforcing()
    {
        await WriteLockFileAsync(_lockFilePath, [new("alpha", "2.0.0", "feed-a", "abc123hash", DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Generate);
        var pkg = Pkg("alpha", "1.0.0", "feed-a");

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("1.0.0", result.EffectivePackage!.Version); // not overridden
    }

    [Fact]
    public async Task EvaluateAsync_StrictModeRequireEntry_MissingEntryBlocked()
    {
        File.Delete(_lockFilePath); // no lock file → missing entry
        var coordinator = Build(_lockFilePath, LockFileMode.Strict, requireEntry: true);
        var pkg = Pkg("alpha", "1.0.0", "feed-a");

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("strict-missing-entry", result.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_StrictModeWithEntry_Permitted()
    {
        await WriteLockFileAsync(_lockFilePath, [new("alpha", "1.0.0", "feed-a", "hash1", DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Strict, requireEntry: true);
        var pkg = Pkg("alpha", "1.0.0", "feed-a");

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    private static LockFileCoordinator Build(string path, LockFileMode mode, bool requireEntry = false) =>
        new(new(path),
            new() { Mode = mode, RequireEntryInStrictMode = requireEntry });

    private static ResolvedPackage Pkg(string id, string version, string feed) =>
        new(id, version, feed, $"/store/{id}", DateTimeOffset.UtcNow, id);

    private static async Task WriteLockFileAsync(string path, IReadOnlyList<PackageLockEntry> packages)
    {
        var lockFile = new PackageLockFile("1.0", DateTimeOffset.UtcNow, packages);
        var json = JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json);
    }
}
