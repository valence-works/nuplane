using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Store;

public sealed class StoreLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuplane-store-lock", Guid.NewGuid().ToString("N"));
    private readonly string _stateFilePath;

    public StoreLockTests()
    {
        Directory.CreateDirectory(_root);
        _stateFilePath = Path.Combine(_root, "store-state.json");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // One test deliberately makes a lock file read-only; clear that again so the root deletes.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Acquire_WhenStoreIsFileBacked_TakesTheLockBesideTheStateFile()
    {
        var storeLock = CreateStoreLock();

        using var handle = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.Acquired, handle.Outcome);
        Assert.True(handle.CanProceed);
        Assert.Equal(_stateFilePath + ".lock", handle.LockFilePath);
        Assert.True(File.Exists(_stateFilePath + ".lock"));
    }

    [Fact]
    public void Acquire_WhenStoreIsFileBacked_DoesNotCreateTheStateFile()
    {
        var storeLock = CreateStoreLock();

        using var handle = storeLock.Acquire();

        Assert.False(File.Exists(_stateFilePath));
    }

    [Fact]
    public void Acquire_WhenAnotherHolderOwnsTheStore_ReportsUnavailableWithoutWaiting()
    {
        var storeLock = CreateStoreLock();
        using var holder = storeLock.Acquire();

        using var contender = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.Unavailable, contender.Outcome);
        Assert.False(contender.CanProceed);
    }

    [Fact]
    public void Acquire_AfterTheHolderReleases_TakesTheLockAgain()
    {
        var storeLock = CreateStoreLock();
        storeLock.Acquire().Dispose();

        using var handle = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.Acquired, handle.Outcome);
    }

    [Fact]
    public void Acquire_WhenTwoIndependentStoreLocksNameTheSameStore_OnlyOneProceeds()
    {
        using var first = CreateStoreLock().Acquire();

        using var second = CreateStoreLock().Acquire();

        Assert.Equal(StoreLockOutcome.Acquired, first.Outcome);
        Assert.Equal(StoreLockOutcome.Unavailable, second.Outcome);
    }

    [Fact]
    public void Acquire_WhenStoreIsInMemory_ReportsNotRequired()
    {
        var storeLock = new StoreLock(
            EffectiveStorePersistenceSettings.Resolve(new() { UseInMemoryStore = true }),
            ReconciliationOptions(enableStoreLock: true),
            NullLogger<StoreLock>.Instance);

        using var handle = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.NotRequired, handle.Outcome);
        Assert.True(handle.CanProceed);
        Assert.Null(handle.LockFilePath);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void Acquire_WhenStoreLockDisabled_ReportsNotRequiredAndWritesNothing()
    {
        var storeLock = CreateStoreLock(enableStoreLock: false);

        using var handle = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.NotRequired, handle.Outcome);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void Acquire_WhenTheLockFileCannotBeCreated_ReportsNotLockableSoTheCycleStillRuns()
    {
        // A file where the state directory should be: creating the directory, and therefore the
        // lock file, cannot succeed. This stands in for a read-only state directory.
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, string.Empty);
        var storeLock = CreateStoreLock(Path.Combine(blocker, "store-state.json"));

        using var handle = storeLock.Acquire();

        Assert.Equal(StoreLockOutcome.NotLockable, handle.Outcome);
        Assert.True(handle.CanProceed);
    }

    [Fact]
    public void Acquire_WhenTheLockFileIsNotWritable_ReportsNotLockableRatherThanContention()
    {
        var lockFilePath = StoreLock.GetLockFilePath(_stateFilePath);
        File.WriteAllText(lockFilePath, string.Empty);
        File.SetAttributes(lockFilePath, FileAttributes.ReadOnly);

        using var handle = CreateStoreLock().Acquire();

        Assert.Equal(StoreLockOutcome.NotLockable, handle.Outcome);
    }

    [Fact]
    public void GetLockFilePath_ForAStateFile_NamesASiblingThatNoEnumerationMatches()
    {
        var lockFilePath = StoreLock.GetLockFilePath("/var/lib/nuplane/.nuplane/store-state.json");

        Assert.Equal("/var/lib/nuplane/.nuplane/store-state.json.lock", lockFilePath);
        Assert.Equal(".lock", Path.GetExtension(lockFilePath));
    }

    private StoreLock CreateStoreLock(string? stateFilePath = null, bool enableStoreLock = true) =>
        new(
            EffectiveStorePersistenceSettings.Resolve(new() { StateFilePath = stateFilePath ?? _stateFilePath }),
            ReconciliationOptions(enableStoreLock),
            NullLogger<StoreLock>.Instance);

    private static IOptions<ReconciliationOptions> ReconciliationOptions(bool enableStoreLock) =>
        new OptionsWrapper<ReconciliationOptions>(new() { EnableStoreLock = enableStoreLock });
}
