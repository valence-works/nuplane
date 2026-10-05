using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class LockFileCycleMiddlewareTests : IDisposable
{
    private static readonly string HashA = CanonicalHash(0);
    private static readonly string HashB = CanonicalHash(1);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-lock-cycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task InvokeAsync_SuccessfulCompleteResolution_GeneratesDeterministicSchemaTwoClosure()
    {
        var path = Path.Combine(_tempRoot, "nuplane.lock.json");
        var coordinator = BuildCoordinator(path);
        var packages = new[]
        {
            Package("z-dependency", "2.0.0", HashB),
            Package("a-root", "1.0.0", HashA)
        };

        await RunSuccessfulCycleAsync(coordinator, packages, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        var firstBytes = await File.ReadAllBytesAsync(path);
        var first = await new LockFileStore(Options(path)).ReadAsync(CancellationToken.None);

        await RunSuccessfulCycleAsync(coordinator, packages, new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero));
        var secondBytes = await File.ReadAllBytesAsync(path);

        Assert.NotNull(first);
        Assert.Equal("2.0", first.SchemaVersion);
        Assert.Equal(["a-root", "z-dependency"], first.Packages.Select(static entry => entry.Id));
        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public async Task InvokeAsync_LegacyLockAndDegradedCycle_DoesNotReplaceFile()
    {
        var path = Path.Combine(_tempRoot, "nuplane.lock.json");
        var options = Options(path);
        var store = new LockFileStore(options);
        await store.WriteAsync(
            new("1.0", DateTimeOffset.UtcNow, [new("legacy", "1.0.0", "feed", "legacy-hash", DateTimeOffset.UtcNow)]),
            CancellationToken.None);
        var before = await File.ReadAllBytesAsync(path);
        var coordinator = new LockFileCoordinator(store, options);
        var middleware = new LockFileCycleMiddleware(coordinator);
        var context = Context(DateTimeOffset.UtcNow);

        await middleware.InvokeAsync(context, () =>
        {
            context.ResolutionResult = new([Package("a-root", "1.0.0", HashA)], ["failed"], []);
            context.Result = new(false, EmptyChangeSet(context.CycleStartedAt), ["failed"], IsDegraded: true);
            return Task.CompletedTask;
        });

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvokeAsync_DownstreamThrows_DoesNotCreateLockFile()
    {
        var path = Path.Combine(_tempRoot, "nuplane.lock.json");
        var middleware = new LockFileCycleMiddleware(BuildCoordinator(path));
        var context = Context(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(
            context,
            () => Task.FromException(new InvalidOperationException("pipeline failed"))));

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static async Task RunSuccessfulCycleAsync(
        LockFileCoordinator coordinator,
        IReadOnlyList<ResolvedPackage> packages,
        DateTimeOffset startedAt)
    {
        var middleware = new LockFileCycleMiddleware(coordinator);
        var context = Context(startedAt);
        await middleware.InvokeAsync(context, () =>
        {
            context.ResolutionResult = new(packages, [], []);
            context.Result = new(false, EmptyChangeSet(startedAt), [], IsDegraded: false);
            return Task.CompletedTask;
        });
    }

    private static ReconciliationCycleContext Context(DateTimeOffset startedAt) => new()
    {
        CorrelationId = "corr-lock",
        CycleStartedAt = startedAt,
        CancellationToken = CancellationToken.None
    };

    private static PackageChangeSet EmptyChangeSet(DateTimeOffset at) => new([], [], [], "corr-lock", at);

    private static ResolvedPackage Package(string id, string version, string hash) =>
        new(id, version, "feed-a", $"/store/{id}/{version}", DateTimeOffset.UtcNow, "source")
        {
            PackageContentHash = hash
        };

    private static string CanonicalHash(byte value) =>
        $"sha512:{Convert.ToBase64String(Enumerable.Repeat(value, 64).ToArray())}";

    private static LockFileCoordinator BuildCoordinator(string path)
    {
        var options = Options(path);
        return new(new LockFileStore(options), options);
    }

    private static OptionsWrapper<LockFileOptions> Options(string path) =>
        new(new LockFileOptions { Mode = LockFileMode.Generate, Path = path });
}
