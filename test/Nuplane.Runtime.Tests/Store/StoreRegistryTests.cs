using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Store;

public sealed class StoreRegistryTests : IDisposable
{
    private const string Recorded = "sha512:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string Other = "sha512:AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ==";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-registry-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PersistActiveVersionsAsync_DescriptorDisagreeingWithTheRecord_PersistsTheRecordedHash()
    {
        var registry = new StoreRegistry(new StoreStateSerializer(), Path.Combine(_root, "state.json"));
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Acme.Addon"] = "1.0.0" };
        await registry.PersistActiveVersionsAsync(versions, versions, "first", CancellationToken.None,
            Descriptors(Recorded), activeGraphs: null);

        // A later write whose descriptor carries a different hash, as a stale or partial cycle could produce.
        await registry.PersistActiveVersionsAsync(versions, versions, "second", CancellationToken.None,
            Descriptors(Other), activeGraphs: null);

        var reloaded = await new StoreRegistry(new StoreStateSerializer(), Path.Combine(_root, "state.json"))
            .GetStateAsync(CancellationToken.None);
        Assert.Equal(Recorded, reloaded.ActivePackageDescriptorsByIdNormalized["Acme.Addon"].ArchiveHash);
        Assert.Equal(Recorded, reloaded.ArchiveHashesByPackage![StoreStateRecord.ArchiveHashKey("Acme.Addon", "1.0.0")]);
    }

    private Dictionary<string, ActivePackage> Descriptors(string hash) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Acme.Addon"] = new("Acme.Addon", "1.0.0", "feed", "source", Path.Combine(_root, "packages", "feed", "Acme.Addon", "1.0.0"),
            DateTimeOffset.UnixEpoch, "correlation") { ArchiveHash = hash }
    };
}
