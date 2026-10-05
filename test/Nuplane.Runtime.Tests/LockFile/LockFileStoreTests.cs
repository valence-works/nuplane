using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.LockFile;

public sealed class LockFileStoreTests
{
    [Fact]
    public async Task WriteAsync_WhenAtomicReplacementFails_PreservesPreviousLockAndCleansTemporaryFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-lock-store-{Guid.NewGuid():N}");
        var path = Path.Combine(tempRoot, "nuplane.lock.json");
        var options = new OptionsWrapper<LockFileOptions>(new() { Path = path });
        var original = new PackageLockFile(
            "2.0",
            DateTimeOffset.UtcNow,
            [new("a", "1.0.0", "feed", CanonicalHash(0), DateTimeOffset.UtcNow)]);

        try
        {
            await new LockFileStore(options).WriteAsync(original, CancellationToken.None);
            var replacer = new FailingReplacer();
            var sut = new LockFileStore(options, replacer);

            await Assert.ThrowsAsync<IOException>(() => sut.WriteAsync(
                original with { Packages = [new("a", "2.0.0", "feed", CanonicalHash(1), DateTimeOffset.UtcNow)] },
                CancellationToken.None));

            var persisted = await new LockFileStore(options).ReadAsync(CancellationToken.None);
            Assert.Equal("1.0.0", Assert.Single(persisted!.Packages).Version);
            Assert.NotNull(replacer.TemporaryPath);
            Assert.Equal(tempRoot, Path.GetDirectoryName(replacer.TemporaryPath));
            Assert.False(File.Exists(replacer.TemporaryPath));
            Assert.Empty(Directory.GetFiles(tempRoot, "nuplane.lock.json.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string CanonicalHash(byte value) =>
        $"sha512:{Convert.ToBase64String(Enumerable.Repeat(value, 64).ToArray())}";

    private sealed class FailingReplacer : IAtomicFileReplacer
    {
        public string? TemporaryPath { get; private set; }

        public Task ReplaceAsync(
            string temporaryFilePath,
            string destinationFilePath,
            string backupFilePath,
            CancellationToken cancellationToken)
        {
            TemporaryPath = temporaryFilePath;
            throw new IOException("replacement failed");
        }
    }
}
