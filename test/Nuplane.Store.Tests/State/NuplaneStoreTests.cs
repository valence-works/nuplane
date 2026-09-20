using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class NuplaneStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-store-reader", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileMissing_ReturnsEmptyCollection()
    {
        var stateFilePath = Path.Combine(_tempRoot, "store-state.json");

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileMissing_DoesNotCreateFileOrDirectory()
    {
        var stateFilePath = Path.Combine(_tempRoot, "store-state.json");

        await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.False(File.Exists(stateFilePath));
        Assert.False(Directory.Exists(_tempRoot));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenNoActivePackagesRecorded_ReturnsEmptyCollection()
    {
        var stateFilePath = await WriteStateFileAsync(StoreStateRecord.Empty());

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenActivePackagesRecorded_ReturnsIdVersionAndInstallPath()
    {
        var descriptor = new ActivePackageDescriptor(
            "Contoso.Plugin",
            "1.2.3",
            "trusted-feed",
            "manifest-source",
            "/var/lib/nuplane/packages/trusted-feed/Contoso.Plugin/1.2.3",
            DateTimeOffset.Parse("2026-04-08T12:00:00Z"),
            "corr-active");
        var state = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor }
        };
        var stateFilePath = await WriteStateFileAsync(state);

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        var actual = Assert.Single(packages);
        Assert.Equal(descriptor.PackageId, actual.PackageId);
        Assert.Equal(descriptor.Version, actual.Version);
        Assert.Equal(descriptor.InstallPath, actual.InstallPath);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileExists_DoesNotModifyFile()
    {
        var descriptor = new ActivePackageDescriptor(
            "Contoso.Plugin",
            "1.2.3",
            "trusted-feed",
            "manifest-source",
            "/var/lib/nuplane/packages/trusted-feed/Contoso.Plugin/1.2.3",
            DateTimeOffset.Parse("2026-04-08T12:00:00Z"),
            "corr-active");
        var state = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor }
        };
        var stateFilePath = await WriteStateFileAsync(state);
        var bytesBefore = await File.ReadAllBytesAsync(stateFilePath);
        var lastWriteTimeBefore = File.GetLastWriteTimeUtc(stateFilePath);

        await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        var bytesAfter = await File.ReadAllBytesAsync(stateFilePath);
        var lastWriteTimeAfter = File.GetLastWriteTimeUtc(stateFilePath);
        Assert.Equal(bytesBefore, bytesAfter);
        Assert.Equal(lastWriteTimeBefore, lastWriteTimeAfter);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileIsCorrupt_Throws()
    {
        var stateFilePath = Path.Combine(_tempRoot, "store-state.json");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(stateFilePath, "{ not valid json");

        await Assert.ThrowsAsync<JsonException>(() =>
            NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileIsEmpty_Throws()
    {
        var stateFilePath = Path.Combine(_tempRoot, "store-state.json");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(stateFilePath, string.Empty);

        await Assert.ThrowsAsync<JsonException>(() =>
            NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WithNullOrWhiteSpacePath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NuplaneStore.ReadActivePackagesAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WithConfiguredPathOptions_ReadsResolvedEffectivePath()
    {
        var descriptor = new ActivePackageDescriptor(
            "Contoso.Plugin",
            "1.2.3",
            "trusted-feed",
            "manifest-source",
            "/var/lib/nuplane/packages/trusted-feed/Contoso.Plugin/1.2.3",
            DateTimeOffset.Parse("2026-04-08T12:00:00Z"),
            "corr-active");
        var state = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor }
        };
        var stateFilePath = await WriteStateFileAsync(state, fileName: "custom-state.json");
        var options = new StoreRegistryOptions { StateFilePath = stateFilePath };

        var packages = await NuplaneStore.ReadActivePackagesAsync(options, CancellationToken.None);

        var actual = Assert.Single(packages);
        Assert.Equal(descriptor.PackageId, actual.PackageId);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WithInMemoryOptions_Throws()
    {
        var options = new StoreRegistryOptions { UseInMemoryStore = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NuplaneStore.ReadActivePackagesAsync(options, CancellationToken.None));
    }

    [Fact]
    public void ReadActivePackagesAsync_WithNullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = NuplaneStore.ReadActivePackagesAsync((StoreRegistryOptions)null!, CancellationToken.None);
        });
    }

    private async Task<string> WriteStateFileAsync(StoreStateRecord state, string fileName = "store-state.json")
    {
        var stateFilePath = Path.Combine(_tempRoot, fileName);
        await new StoreStateSerializer().SaveAsync(stateFilePath, state, CancellationToken.None);
        return stateFilePath;
    }
}
