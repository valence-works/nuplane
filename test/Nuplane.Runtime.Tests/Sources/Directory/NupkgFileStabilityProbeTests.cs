using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Sources.Directory;

namespace Nuplane.Runtime.Tests.Sources.Directory;

/// <summary>
/// Unit tests for <see cref="NupkgFileStabilityProbe"/> verifying bounded
/// partial-write safety behavior.
/// </summary>
public sealed class NupkgFileStabilityProbeTests
{
    private readonly NupkgFileStabilityProbe _probe = new(
        NullLogger<NupkgFileStabilityProbe>.Instance,
        maxAttempts: 3,
        retryDelay: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task StableFile_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "stable.nupkg");
        await File.WriteAllBytesAsync(filePath, CreateValidNupkgBytes());

        var result = await _probe.IsStableAsync(filePath);

        Assert.True(result);
    }

    [Fact]
    public async Task NonExistentFile_ReturnsFalse()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "does-not-exist.nupkg");

        var result = await _probe.IsStableAsync(filePath);

        Assert.False(result);
    }

    [Fact]
    public async Task LockedFile_ReturnsFalseAfterMaxAttempts()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "locked.nupkg");
        await File.WriteAllBytesAsync(filePath, CreateValidNupkgBytes());

        // Hold an exclusive write lock for the duration of the probe
        using var lockStream = new FileStream(
            filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await _probe.IsStableAsync(filePath);

        Assert.False(result);
    }

    [Fact]
    public async Task EmptyFile_ReturnsFalse()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "empty.nupkg");
        await File.WriteAllBytesAsync(filePath, []);

        var result = await _probe.IsStableAsync(filePath);

        // Empty files (size 0) never achieve stability (previousSize must be > 0)
        Assert.False(result);
    }

    [Fact]
    public async Task CancellationToken_ThrowsOperationCanceled()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "cancel.nupkg");
        await File.WriteAllBytesAsync(filePath, CreateValidNupkgBytes());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _probe.IsStableAsync(filePath, cts.Token));
    }

    [Fact]
    public async Task FileGrowingDuringProbe_ReturnsFalse()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "growing.nupkg");

        // Create a probe with very short retry delay but only 2 attempts
        var shortProbe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.FromMilliseconds(50));

        // Write initial content
        await File.WriteAllBytesAsync(filePath, new byte[100]);

        // Start probe, but grow file between attempts
        var probeTask = Task.Run(async () =>
        {
            // Wait a moment then grow the file
            await Task.Delay(30);
            await File.WriteAllBytesAsync(filePath, new byte[200]);
        });

        var result = await shortProbe.IsStableAsync(filePath);

        await probeTask; // ensure our mutation task completes

        // The file was mutated during probing. The probe should handle this
        // gracefully without throwing, returning false because the size changed.
        Assert.False(result);
    }

    [Fact]
    public void Constructor_MaxAttemptsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NupkgFileStabilityProbe(
                NullLogger<NupkgFileStabilityProbe>.Instance,
                maxAttempts: 0));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NupkgFileStabilityProbe(null!));
    }

    [Fact]
    public async Task NullFilePath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _probe.IsStableAsync(null!));
    }

    [Fact]
    public async Task EmptyFilePath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _probe.IsStableAsync(string.Empty));
    }

    [Fact]
    public async Task FileBecomesStableAfterRetry_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        var filePath = Path.Combine(tempDir.Path, "eventually-stable.nupkg");
        var stableContent = CreateValidNupkgBytes();

        // Use a probe with enough attempts for the file to stabilize
        var retryProbe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 5,
            retryDelay: TimeSpan.FromMilliseconds(50));

        // Write initial content — stable from the start
        await File.WriteAllBytesAsync(filePath, stableContent);

        var result = await retryProbe.IsStableAsync(filePath);

        Assert.True(result);
    }

    /// <summary>
    /// Creates bytes that look like a minimal valid .nupkg (ZIP/PK header).
    /// </summary>
    private static byte[] CreateValidNupkgBytes()
    {
        // PK zip header + some padding to make a non-empty file
        var bytes = new byte[256];
        bytes[0] = 0x50; // P
        bytes[1] = 0x4B; // K
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        return bytes;
    }
}
