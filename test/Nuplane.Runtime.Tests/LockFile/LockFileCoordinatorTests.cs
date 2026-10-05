using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Runtime.Tests.LockFile;

public sealed class LockFileCoordinatorTests : IDisposable
{
    private static readonly string HashA = CanonicalHash(0);
    private static readonly string HashB = CanonicalHash(1);
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
        var pkg = Pkg("alpha", "1.0.0", "feed-a") with { PackageContentHash = HashA };

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("enforce-no-entry", result.ReasonCode);
    }

    [Fact]
    public async Task CaptureAsync_EnforceMode_ConstrainsVersionAndFeedBeforeResolution()
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", [new("alpha", "2.0.0", "feed-a", HashA, DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);

        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var request = coordinator.ConstrainRequest(
            snapshot,
            new("alpha", "[1.0.0, 3.0.0)", "feed-live", PackageUpdatePolicy.Range, "source"));

        Assert.Equal("2.0.0", request.VersionRange);
        Assert.Equal("feed-a", request.FeedName);
        Assert.Equal(PackageUpdatePolicy.Exact, request.UpdatePolicy);
    }

    [Fact]
    public async Task EvaluateAsync_GenerateMode_PassesThroughWithoutEnforcing()
    {
        await WriteLockFileAsync(_lockFilePath, "1.0", [new("alpha", "2.0.0", "feed-a", "legacy", DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Generate);
        var pkg = Pkg("alpha", "1.0.0", "feed-a") with { PackageContentHash = HashA };

        var result = await coordinator.EvaluateAsync(pkg, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("1.0.0", result.EffectivePackage!.Version); // not overridden
    }

    [Fact]
    public async Task EvaluateAsync_GenerateModeMissingActualHash_BlocksBeforeActivation()
    {
        File.Delete(_lockFilePath);
        var coordinator = Build(_lockFilePath, LockFileMode.Generate);

        var result = await coordinator.EvaluateAsync(Pkg("alpha", "1.0.0", "feed-a"), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("actual-hash-missing", result.ReasonCode);
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
    public async Task CaptureAsync_StrictModeWithCanonicalEntry_VerifiesArtifact()
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", [new("alpha", "1.0.0", "feed-a", HashA, DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Strict, requireEntry: true);
        var pkg = Pkg("alpha", "1.0.0", "feed-a") with { PackageContentHash = HashA };

        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var result = coordinator.Evaluate(snapshot, pkg);

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("3.0")]
    public async Task CaptureAsync_EnforceModeUnsupportedSchema_RejectsBeforeAcquisition(string schemaVersion)
    {
        await WriteLockFileAsync(_lockFilePath, schemaVersion, [new("alpha", "1.0.0", "feed-a", HashA, DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);

        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);

        var exception = Assert.Throws<LockFilePolicyException>(() => coordinator.ConstrainRequest(
            snapshot,
            new("alpha", "1.0.0", null, PackageUpdatePolicy.Exact, "source")));
        Assert.Equal("unsupported-schema", exception.ReasonCode);
        Assert.Contains("Generate", exception.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":\"2.0\",\"generatedAt\":\"2026-10-05T00:00:00Z\",\"packages\":null}")]
    public async Task CaptureAsync_EnforceModeMalformedLock_RejectsWithPolicyDiagnostic(string contents)
    {
        await File.WriteAllTextAsync(_lockFilePath, contents);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);

        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);

        var exception = Assert.Throws<LockFilePolicyException>(() => coordinator.ConstrainRequest(
            snapshot,
            new("alpha", "1.0.0", null, PackageUpdatePolicy.Exact, "source")));
        Assert.Equal("invalid-lock-file", exception.ReasonCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha512:not-base64")]
    [InlineData("SHA512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ConstrainRequest_UsedEntryHasInvalidHash_RejectsBeforeAcquisition(string hash)
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", [new("alpha", "1.0.0", "feed-a", hash, DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);

        var exception = Assert.Throws<LockFilePolicyException>(() => coordinator.ConstrainRequest(
            snapshot,
            new("alpha", "1.0.0", null, PackageUpdatePolicy.Exact, "source")));

        Assert.Equal("invalid-hash", exception.ReasonCode);
    }

    [Fact]
    public async Task Evaluate_ArtifactHashMismatch_RejectsBeforeActivation()
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", [new("alpha", "1.0.0", "feed-a", HashA, DateTimeOffset.UtcNow)]);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);

        var result = coordinator.Evaluate(
            snapshot,
            Pkg("alpha", "1.0.0", "feed-a") with { PackageContentHash = HashB });

        Assert.False(result.Allowed);
        Assert.Equal("hash-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task ConstrainRequest_EnforceModeMissingEntry_PreservesLiveRequest()
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", []);
        var coordinator = Build(_lockFilePath, LockFileMode.Enforce);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var live = new PackageRequest("alpha", "[1.0.0, 2.0.0)", "feed-live", PackageUpdatePolicy.Range, "source");

        var constrained = coordinator.ConstrainRequest(snapshot, live);

        Assert.Same(live, constrained);
    }

    [Fact]
    public async Task ConstrainRequest_StrictModeMissingDependencyEntry_RejectsBeforeResolverCall()
    {
        await WriteLockFileAsync(_lockFilePath, "2.0", []);
        var coordinator = Build(_lockFilePath, LockFileMode.Strict, requireEntry: true);
        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);

        var exception = Assert.Throws<LockFilePolicyException>(() => coordinator.ConstrainRequest(
            snapshot,
            new("dependency", "[1.0.0]", null, PackageUpdatePolicy.Exact, "dependency-of:root")));

        Assert.Equal("strict-missing-entry", exception.ReasonCode);
    }

    private static LockFileCoordinator Build(string path, LockFileMode mode, bool requireEntry = false)
    {
        var options = new LockFileOptions { Path = path, Mode = mode, RequireEntryInStrictMode = requireEntry };
        return new(
            new(new OptionsWrapper<LockFileOptions>(options)),
            new OptionsWrapper<LockFileOptions>(options));
    }

    private static ResolvedPackage Pkg(string id, string version, string feed) =>
        new(id, version, feed, $"/store/{id}", DateTimeOffset.UtcNow, id);

    private static string CanonicalHash(byte value) =>
        $"sha512:{Convert.ToBase64String(Enumerable.Repeat(value, 64).ToArray())}";

    private static Task WriteLockFileAsync(string path, IReadOnlyList<PackageLockEntry> packages) =>
        WriteLockFileAsync(path, "1.0", packages);

    private static async Task WriteLockFileAsync(string path, string schemaVersion, IReadOnlyList<PackageLockEntry> packages)
    {
        var lockFile = new PackageLockFile(schemaVersion, DateTimeOffset.UtcNow, packages);
        var json = JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json);
    }
}
