using Nuplane.Store.State;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Nuplane.Store.Tests.State;

public sealed class StoreRegistryTests
{
    [Fact]
    public async Task PersistActiveVersions_WhenSaveFails_Throws()
    {
        var serializer = Substitute.For<IStoreStateSerializer>();
        serializer.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(StoreStateRecord.Empty());
        serializer.SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var registry = new StoreRegistry(serializer, stateFilePath: "/tmp/state.json");

        await Assert.ThrowsAsync<IOException>(() =>
            registry.PersistActiveVersionsAsync(
                new Dictionary<string, string> { ["pkg"] = "1.0.0" },
                new Dictionary<string, string> { ["pkg"] = "1.0.0" },
                "corr-001",
                CancellationToken.None));
    }

    [Fact]
    public async Task PersistFailure_WhenSaveFails_Throws()
    {
        var serializer = Substitute.For<IStoreStateSerializer>();
        serializer.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(StoreStateRecord.Empty());
        serializer.SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var registry = new StoreRegistry(serializer, stateFilePath: "/tmp/state.json");

        await Assert.ThrowsAsync<IOException>(() =>
            registry.PersistFailureAsync("pkg", "stage", "err", "corr-002", CancellationToken.None));
    }

    [Fact]
    public async Task PersistSourceSnapshot_WhenSaveFails_Throws()
    {
        var serializer = Substitute.For<IStoreStateSerializer>();
        serializer.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(StoreStateRecord.Empty());
        serializer.SaveAsync(Arg.Any<string>(), Arg.Any<StoreStateRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Disk full"));

        var registry = new StoreRegistry(serializer, stateFilePath: "/tmp/state.json");

        await Assert.ThrowsAsync<IOException>(() =>
            registry.PersistSourceSnapshotAsync(
                "local",
                new SourceSnapshotRef("v1", DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryMode_DoesNotCallSerializer_OnPersist()
    {
        var serializer = Substitute.For<IStoreStateSerializer>();
        var registry = new StoreRegistry(serializer, stateFilePath: null);

        await registry.PersistActiveVersionsAsync(
            new Dictionary<string, string> { ["pkg"] = "1.0.0" },
            new Dictionary<string, string> { ["pkg"] = "1.0.0" },
            "corr-003",
            CancellationToken.None);

        await serializer.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<StoreStateRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Resolve_WithDefaultOptions_ReturnsDefaultPathMode()
    {
        var settings = EffectiveStorePersistenceSettings.Resolve(new StoreRegistryOptions());

        Assert.Equal(StorePersistenceMode.DefaultPath, settings.Mode);
    }

    [Fact]
    public void Resolve_WithDefaultOptions_ResolvesPathUnderBaseDirectory()
    {
        var settings = EffectiveStorePersistenceSettings.Resolve(new StoreRegistryOptions());

        Assert.NotNull(settings.ResolvedStateFilePath);
        var expected = Path.Combine(AppContext.BaseDirectory, ".nuplane", "store-state.json");
        Assert.Equal(expected, settings.ResolvedStateFilePath);
    }

    [Fact]
    public async Task DefaultPath_SaveThenLoad_RestoresState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-default-path", Guid.NewGuid().ToString("N"));

        try
        {
            var stateFilePath = Path.Combine(tempRoot, ".nuplane", "store-state.json");
            var serializer = new StoreStateSerializer();

            var registry = new StoreRegistry(serializer, stateFilePath);

            await registry.PersistActiveVersionsAsync(
                new Dictionary<string, string> { ["pkg-a"] = "2.0.0" },
                new Dictionary<string, string> { ["pkg-a"] = "2.0.0" },
                "corr-default",
                CancellationToken.None);

            var registry2 = new StoreRegistry(serializer, stateFilePath);
            var state = await registry2.GetStateAsync(CancellationToken.None);

            Assert.True(state.ActiveVersionById.ContainsKey("pkg-a"));
            Assert.Equal("2.0.0", state.ActiveVersionById["pkg-a"]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConfiguredPath_SaveThenLoad_RestoresState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-configured-path", Guid.NewGuid().ToString("N"));

        try
        {
            var stateFilePath = Path.Combine(tempRoot, "custom-state.json");
            var serializer = new StoreStateSerializer();

            var registry = new StoreRegistry(serializer, stateFilePath);

            await registry.PersistActiveVersionsAsync(
                new Dictionary<string, string> { ["pkg-b"] = "3.0.0" },
                new Dictionary<string, string> { ["pkg-b"] = "3.0.0" },
                "corr-configured",
                CancellationToken.None);

            var registry2 = new StoreRegistry(serializer, stateFilePath);
            var state = await registry2.GetStateAsync(CancellationToken.None);

            Assert.True(state.ActiveVersionById.ContainsKey("pkg-b"));
            Assert.Equal("3.0.0", state.ActiveVersionById["pkg-b"]);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_WithUseInMemoryStore_ReturnsInMemoryMode()
    {
        var settings = EffectiveStorePersistenceSettings.Resolve(
            new StoreRegistryOptions { UseInMemoryStore = true });

        Assert.Equal(StorePersistenceMode.InMemory, settings.Mode);
        Assert.Null(settings.ResolvedStateFilePath);
        Assert.True(settings.UseInMemoryStore);
    }

    [Fact]
    public async Task InMemoryMode_DoesNotCreateStateFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-inmemory-nofile", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "should-not-exist.json");

        try
        {
            var registry = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);

            await registry.PersistActiveVersionsAsync(
                new Dictionary<string, string> { ["pkg"] = "1.0.0" },
                new Dictionary<string, string> { ["pkg"] = "1.0.0" },
                "corr-inmemory",
                CancellationToken.None);

            Assert.False(File.Exists(stateFilePath));
            Assert.False(Directory.Exists(tempRoot));
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InMemoryMode_StartsEmpty_AfterRestart()
    {
        var serializer = Substitute.For<IStoreStateSerializer>();
        var registry = new StoreRegistry(serializer, stateFilePath: null);

        await registry.PersistActiveVersionsAsync(
            new Dictionary<string, string> { ["pkg"] = "1.0.0" },
            new Dictionary<string, string> { ["pkg"] = "1.0.0" },
            "corr-session1",
            CancellationToken.None);

        // Simulate restart: create new registry with null path
        var registry2 = new StoreRegistry(serializer, stateFilePath: null);
        var state = await registry2.GetStateAsync(CancellationToken.None);

        Assert.Empty(state.ActiveVersionById);
    }
}
