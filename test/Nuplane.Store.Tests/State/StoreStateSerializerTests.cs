using System.Collections;
using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class StoreStateSerializerTests
{
    [Fact]
    public async Task SaveAsync_WhenReplacementFails_PreservesDiskAndMemoryAndCleansUniqueTemporaryFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-state-replace-failure", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");
        var defaultSerializer = new StoreStateSerializer();
        var previousState = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" }
        };

        try
        {
            await defaultSerializer.SaveAsync(stateFilePath, previousState, CancellationToken.None);
            var replacer = new FailingFileReplacer();
            var registry = new StoreRegistry(new StoreStateSerializer(replacer), stateFilePath);
            await registry.GetStateAsync(CancellationToken.None);

            await Assert.ThrowsAsync<IOException>(() => registry.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "2.0.0" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "2.0.0" },
                "corr-replace-active",
                CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(() => registry.PersistFailureAsync(
                "pkg-b",
                "resolve",
                "failed",
                "corr-replace-failure",
                CancellationToken.None));

            var stateAfterFailure = await registry.GetStateAsync(CancellationToken.None);
            var diskStateAfterFailure = await defaultSerializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal("1.0.0", stateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Empty(stateAfterFailure.LastFailureById);
            Assert.Equal("1.0.0", diskStateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Equal(2, replacer.TemporaryFilePaths.Count);
            Assert.Equal(2, replacer.TemporaryFilePaths.Distinct(StringComparer.Ordinal).Count());
            Assert.All(replacer.TemporaryFilePaths, path => Assert.Equal(tempRoot, Path.GetDirectoryName(path)));
            Assert.All(replacer.TemporaryFilePaths, path => Assert.False(File.Exists(path)));
            Assert.Empty(Directory.GetFiles(tempRoot, "store-state.json.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WhenCancelledBeforeReplacement_PreservesPreviousRecordAndCleansTemporaryFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-state-cancelled-replace", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");
        var defaultSerializer = new StoreStateSerializer();
        var previousState = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" }
        };

        try
        {
            await defaultSerializer.SaveAsync(stateFilePath, previousState, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var temporaryFiles = new List<string>();
            var registry = new StoreRegistry(
                new StoreStateSerializer(new CancellingFileReplacer(cancellation, temporaryFiles)),
                stateFilePath);
            await registry.GetStateAsync(CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "2.0.0" },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "2.0.0" },
                "corr-cancelled-replace",
                cancellation.Token));

            var stateAfterFailure = await defaultSerializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal("1.0.0", stateAfterFailure.ActiveVersionById["pkg-a"]);
            var inMemoryStateAfterFailure = await registry.GetStateAsync(CancellationToken.None);
            Assert.Equal("1.0.0", inMemoryStateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Single(temporaryFiles);
            Assert.False(File.Exists(temporaryFiles[0]));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_ConcurrentReaderDuringReplacement_ObservesCompleteOldAndNewRecords()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-state-concurrent-reader", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");
        var defaultSerializer = new StoreStateSerializer();
        var previousState = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" }
        };
        var nextState = previousState with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "2.0.0" }
        };

        try
        {
            await defaultSerializer.SaveAsync(stateFilePath, previousState, CancellationToken.None);
            var replacer = new PausingFileReplacer();
            var writer = new StoreStateSerializer(replacer);
            var saveTask = writer.SaveAsync(stateFilePath, nextState, CancellationToken.None);
            await replacer.ReplaceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await using var reader = new FileStream(
                stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var duringReplacement = await defaultSerializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal("1.0.0", duringReplacement.ActiveVersionById["pkg-a"]);

            replacer.AllowReplace.TrySetResult();
            await saveTask.WaitAsync(TimeSpan.FromSeconds(5));

            var fromOpenReader = await StoreStateSerializer.DeserializeAsync(reader, CancellationToken.None);
            var afterReplacement = await defaultSerializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal("1.0.0", fromOpenReader.ActiveVersionById["pkg-a"]);
            Assert.Equal("2.0.0", afterReplacement.ActiveVersionById["pkg-a"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WhenSerializationFails_LeavesPreviousRecordReadableAndCleansTemporaryFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-state-serializer", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");
        var serializer = new StoreStateSerializer();

        try
        {
            await serializer.SaveAsync(
                stateFilePath,
                StoreStateRecord.Empty() with
                {
                    ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" }
                },
                CancellationToken.None);

            var invalidState = StoreStateRecord.Empty() with
            {
                LastSuccessfulSourceSnapshots = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["source-a"] = new(
                        "snapshot-a",
                        DateTimeOffset.UtcNow,
                        new ThrowingPackageRequestList())
                }
            };

            await Assert.ThrowsAsync<IOException>(() =>
                serializer.SaveAsync(stateFilePath, invalidState, CancellationToken.None));

            var stateAfterFailure = await serializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal("1.0.0", stateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Empty(Directory.GetFiles(tempRoot, "store-state.json.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StoreRegistry_WhenSerializationFails_PreservesDiskAndMemory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-state-registry-serialization-failure", Guid.NewGuid().ToString("N"));
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");
        var serializer = new StoreStateSerializer();
        var previousState = StoreStateRecord.Empty() with
        {
            ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["pkg-a"] = "1.0.0" }
        };

        try
        {
            await serializer.SaveAsync(stateFilePath, previousState, CancellationToken.None);
            var registry = new StoreRegistry(serializer, stateFilePath);
            var initialState = await registry.GetStateAsync(CancellationToken.None);

            await Assert.ThrowsAsync<IOException>(() => registry.PersistSourceSnapshotAsync(
                "source-a",
                new("snapshot-a", DateTimeOffset.UtcNow, new ThrowingPackageRequestList()),
                CancellationToken.None));

            var stateAfterFailure = await registry.GetStateAsync(CancellationToken.None);
            var diskStateAfterFailure = await serializer.LoadAsync(stateFilePath, CancellationToken.None);
            Assert.Equal(initialState.UpdatedAt, stateAfterFailure.UpdatedAt);
            Assert.Equal("1.0.0", stateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Empty(stateAfterFailure.LastSuccessfulSourceSnapshots);
            Assert.Equal("1.0.0", diskStateAfterFailure.ActiveVersionById["pkg-a"]);
            Assert.Empty(diskStateAfterFailure.LastSuccessfulSourceSnapshots);
            Assert.Empty(Directory.GetFiles(tempRoot, "store-state.json.*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class ThrowingPackageRequestList : IReadOnlyList<PackageRequest>
    {
        public int Count => 2;

        public PackageRequest this[int index] => index == 0
            ? new("pkg-a", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "source-a")
            : throw new IOException("Injected serialization failure.");

        public IEnumerator<PackageRequest> GetEnumerator()
        {
            yield return this[0];
            throw new IOException("Injected serialization failure.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FailingFileReplacer : IAtomicFileReplacer
    {
        public List<string> TemporaryFilePaths { get; } = [];

        public Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken)
        {
            TemporaryFilePaths.Add(temporaryFilePath);
            return Task.FromException(new IOException("Injected atomic replacement failure."));
        }
    }

    private sealed class CancellingFileReplacer(
        CancellationTokenSource cancellation,
        List<string> temporaryFilePaths) : IAtomicFileReplacer
    {
        public Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken)
        {
            temporaryFilePaths.Add(temporaryFilePath);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class PausingFileReplacer : IAtomicFileReplacer
    {
        public TaskCompletionSource ReplaceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReplace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken)
        {
            ReplaceStarted.TrySetResult();
            await AllowReplace.Task.WaitAsync(cancellationToken);
            File.Move(temporaryFilePath, destinationFilePath, overwrite: true);
        }
    }
}
