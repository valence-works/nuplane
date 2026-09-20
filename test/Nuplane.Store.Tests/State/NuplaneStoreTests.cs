using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class NuplaneStoreTests : IDisposable
{
    private static readonly ActivePackageDescriptor DefaultDescriptor = new(
        "Contoso.Plugin",
        "1.2.3",
        "trusted-feed",
        "manifest-source",
        "/var/lib/nuplane/packages/trusted-feed/Contoso.Plugin/1.2.3",
        DateTimeOffset.Parse("2026-04-08T12:00:00Z"),
        "corr-active");

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-store-reader", Guid.NewGuid().ToString("N"));
    private readonly StoreStateSerializer _serializer = new();

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
    public async Task ReadActivePackagesAsync_WhenParentDirectoryMissing_ReturnsEmptyCollection()
    {
        var stateFilePath = Path.Combine(_tempRoot, "nested", "store-state.json");

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
        Assert.False(Directory.Exists(_tempRoot));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenNoActivePackagesRecorded_ReturnsEmptyCollection()
    {
        var stateFilePath = await WriteStateAsync(StoreStateRecord.Empty());

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenActivePackagesRecorded_ReturnsIdVersionAndInstallPath()
    {
        var stateFilePath = await WriteActivePackageAsync(DefaultDescriptor);

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        var actual = Assert.Single(packages);
        Assert.Equal(DefaultDescriptor.PackageId, actual.PackageId);
        Assert.Equal(DefaultDescriptor.Version, actual.Version);
        Assert.Equal(DefaultDescriptor.InstallPath, actual.InstallPath);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenDescriptorVersionDivergesFromActiveVersion_OmitsPackage()
    {
        var state = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { [DefaultDescriptor.PackageId] = "9.9.9" },
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [DefaultDescriptor.PackageId] = DefaultDescriptor }
        };
        var stateFilePath = await WriteStateAsync(state);

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenDescriptorHasNoActiveVersionEntry_OmitsPackage()
    {
        var state = StoreStateRecord.Empty() with
        {
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [DefaultDescriptor.PackageId] = DefaultDescriptor }
        };
        var stateFilePath = await WriteStateAsync(state);

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        Assert.Empty(packages);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileExists_DoesNotModifyFile()
    {
        var stateFilePath = await WriteActivePackageAsync(DefaultDescriptor);
        var bytesBefore = await File.ReadAllBytesAsync(stateFilePath);
        var lastWriteTimeBefore = File.GetLastWriteTimeUtc(stateFilePath);

        await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        var bytesAfter = await File.ReadAllBytesAsync(stateFilePath);
        var lastWriteTimeAfter = File.GetLastWriteTimeUtc(stateFilePath);
        Assert.Equal(bytesBefore, bytesAfter);
        Assert.Equal(lastWriteTimeBefore, lastWriteTimeAfter);
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileHeldOpenExclusively_ThrowsIOException()
    {
        var stateFilePath = await WriteActivePackageAsync(DefaultDescriptor);
        await using var exclusiveHandle = new FileStream(stateFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() =>
            NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None));
    }

    [Fact]
    public async Task ReadActivePackagesAsync_WhenFileHeldOpenByWriterWithReadWriteShare_ReadsSuccessfully()
    {
        var stateFilePath = await WriteActivePackageAsync(DefaultDescriptor);
        await using var writerHandle = new FileStream(stateFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var packages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath, CancellationToken.None);

        var actual = Assert.Single(packages);
        Assert.Equal(DefaultDescriptor.PackageId, actual.PackageId);
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
        var stateFilePath = await WriteActivePackageAsync(DefaultDescriptor, fileName: "custom-state.json");
        var options = new StoreRegistryOptions { StateFilePath = stateFilePath };

        var packages = await NuplaneStore.ReadActivePackagesAsync(options, CancellationToken.None);

        var actual = Assert.Single(packages);
        Assert.Equal(DefaultDescriptor.PackageId, actual.PackageId);
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

    /// <summary>
    /// Writes <paramref name="descriptor"/> as the sole active package descriptor, with a
    /// matching <see cref="StoreStateRecord.ActiveVersionById"/> entry, matching how a running
    /// host keeps both dictionaries in sync for an active package.
    /// </summary>
    private Task<string> WriteActivePackageAsync(ActivePackageDescriptor descriptor, string fileName = "store-state.json")
    {
        var state = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
            ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor }
        };

        return WriteStateAsync(state, fileName);
    }

    private async Task<string> WriteStateAsync(StoreStateRecord state, string fileName = "store-state.json")
    {
        var stateFilePath = Path.Combine(_tempRoot, fileName);
        await _serializer.SaveAsync(stateFilePath, state, CancellationToken.None);
        return stateFilePath;
    }
}
