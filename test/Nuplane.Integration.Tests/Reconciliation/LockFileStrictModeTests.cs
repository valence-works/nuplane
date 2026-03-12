using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class LockFileStrictModeTests
{
    [Fact]
    public async Task Evaluate_WhenStrictModeAndEntryMissing_BlocksPackage()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"nuplane-lock-{Guid.NewGuid():N}.json");
        var lockOptions = new LockFileOptions { Mode = LockFileMode.Strict, Path = lockPath, RequireEntryInStrictMode = true };
        var store = new LockFileStore(new OptionsWrapper<LockFileOptions>(lockOptions));
        await store.WriteAsync(new("1.0", DateTimeOffset.UtcNow, []), CancellationToken.None);

        var coordinator = new LockFileCoordinator(
            store,
            new OptionsWrapper<LockFileOptions>(lockOptions));

        var resolved = new ResolvedPackage("pkg-missing", "1.0.0", "feed-live", "/tmp/pkg-missing", DateTimeOffset.UtcNow, "source");
        var result = await coordinator.EvaluateAsync(resolved, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("strict-missing-entry", result.ReasonCode);
    }
}
