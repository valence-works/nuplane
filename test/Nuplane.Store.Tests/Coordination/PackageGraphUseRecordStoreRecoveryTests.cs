using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class PackageGraphUseRecordStoreTests
{
    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_EmptyNamespaceIsAnIdempotentNoOp()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();

        var result = await new PackageGraphUseRecordStore(fixture.Files).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Empty(result.RecoveredUseIds);
        Assert.Empty(result.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_StalePublishedGraphReplaysThenRemovesExactPair()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();

        var result = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Equal([owner.Record.UseId], result.RecoveredUseIds);
        Assert.Empty(result.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
        Assert.Empty((await store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None)).Entries);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_ActualPublishedLiveOwnerIsPreservedBeforeAnyRemovalTokenAttempt()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var publisher = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await publisher.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Pending, CancellationToken.None);
        var originalNames = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var hooks = new PublicationHooks(fixture.Files);
        var recoveryStore = new PackageGraphUseRecordStore(hooks);

        try
        {
            var live = await recoveryStore.RecoverAsync(
                fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

            Assert.Empty(live.RecoveredUseIds);
            Assert.Equal([owner.Record.UseId], live.LiveUseIds);
            Assert.Equal(0, hooks.RemovalTokenAttempts);
            Assert.Equal(originalNames, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
            using var sentinel = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.SentinelName, FileAccess.ReadWrite);
            Assert.Null(await fixture.Files.TryAcquireExclusiveLock(sentinel));
        }
        finally
        {
            await owner.DisposeAsync();
        }

        var recovered = await recoveryStore.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);
        Assert.Equal([owner.Record.UseId], recovered.RecoveredUseIds);
        Assert.Empty(recovered.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("reaping")]
    [InlineData("deleting")]
    public async Task RecoverAsync_InterruptedPublishedJournalResumesFromExactPhase(string phase)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        MoveArtifact(fixture, owner.RecordName, $"use-{owner.Record.UseId:N}.{phase}");

        var result = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Equal([owner.Record.UseId], result.RecoveredUseIds);
        Assert.Empty(result.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("reaping")]
    [InlineData("deleting")]
    public async Task RecoverAsync_LivePublishedJournalRestoresCanonicalJsonWithoutReplacement(string phase)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        MoveArtifact(fixture, owner.RecordName, $"use-{owner.Record.UseId:N}.{phase}");
        var originalRecord = File.ReadAllBytes(Path.Combine(fixture.ControlPath, $"use-{owner.Record.UseId:N}.{phase}"));
        var hooks = new PublicationHooks(fixture.Files);

        try
        {
            var result = await new PackageGraphUseRecordStore(hooks).RecoverAsync(
                fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

            Assert.Empty(result.RecoveredUseIds);
            Assert.Equal([owner.Record.UseId], result.LiveUseIds);
            Assert.Equal(0, hooks.RemovalTokenAttempts);
            var restoredNames = fixture.UseArtifactNames().Select(static name => name!).ToArray();
            Assert.Equal(new[] { $"use-{owner.Record.UseId:N}.json", owner.SentinelName }
                    .OrderBy(static name => name, StringComparer.Ordinal),
                restoredNames.OrderBy(static name => name, StringComparer.Ordinal));
            Assert.Equal(originalRecord, File.ReadAllBytes(Path.Combine(fixture.ControlPath, owner.RecordName)));
            Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("")]
    [InlineData("truncated")]
    public async Task RecoverAsync_UnpublishedStageMayBeEmptyOrTruncated(string value)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var useId = Guid.NewGuid();
        var sentinelName = $"use-{useId:N}.sentinel";
        var stageName = $"use-{useId:N}.json.stage";
        CreateFile(fixture, sentinelName, []);
        CreateFile(fixture, stageName, value.Length == 0 ? [] : [0x7b, 0x22, 0x76]);

        var result = await new PackageGraphUseRecordStore(fixture.Files).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Equal([useId], result.RecoveredUseIds);
        Assert.Empty(result.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("json.stage")]
    [InlineData("stage-deleting")]
    public async Task RecoverAsync_BusyUnpublishedSentinelPreservesItsExactStagePhase(string phase)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var useId = Guid.NewGuid();
        var sentinelName = $"use-{useId:N}.sentinel";
        var stageName = $"use-{useId:N}.{phase}";
        CreateFile(fixture, sentinelName, []);
        CreateFile(fixture, stageName, [0x7b, 0x22]);
        var originalNames = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var hooks = new PublicationHooks(fixture.Files);
        using var sentinel = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var liveLock = await fixture.Files.TryAcquireExclusiveLock(sentinel);
        Assert.NotNull(liveLock);

        try
        {
            var result = await new PackageGraphUseRecordStore(hooks).RecoverAsync(
                fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

            Assert.Empty(result.RecoveredUseIds);
            Assert.Equal(new[] { useId }, result.LiveUseIds);
            Assert.Equal(0, hooks.RemovalTokenAttempts);
            Assert.Equal(originalNames, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
        }
        finally
        {
            await liveLock!.DisposeAsync();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_SentinelOnlyPublicationResidueIsRemovedAfterFreshLock()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var useId = Guid.NewGuid();
        CreateFile(fixture, $"use-{useId:N}.sentinel", []);

        var result = await new PackageGraphUseRecordStore(fixture.Files).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Equal([useId], result.RecoveredUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("published")]
    [InlineData("stage")]
    public async Task RecoverAsync_TerminalJournalWithPositivelyAbsentSentinelRemovesOnlyMarker(string kind)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var useId = Guid.NewGuid();
        string terminalName;
        if (kind == "published")
        {
            var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
                PackageGraphUseSnapshotState.Committed, CancellationToken.None);
            useId = owner.Record.UseId;
            await owner.DisposeAsync();
            MoveArtifact(fixture, owner.RecordName, $"use-{useId:N}.deleting");
            await RemoveArtifactAsync(fixture, owner.SentinelName);
            terminalName = $"use-{useId:N}.deleting";
        }
        else
        {
            var sentinelName = $"use-{useId:N}.sentinel";
            terminalName = $"use-{useId:N}.stage-deleting";
            CreateFile(fixture, sentinelName, []);
            CreateFile(fixture, terminalName, [0x7b, 0x22]);
            await RemoveArtifactAsync(fixture, sentinelName);
        }

        var result = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Equal([useId], result.RecoveredUseIds);
        Assert.Empty(result.LiveUseIds);
        Assert.Empty(fixture.UseArtifactNames());
        Assert.False(File.Exists(Path.Combine(fixture.ControlPath, terminalName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_UnknownArtifactRefusesBeforeChangingAnyValidStalePair()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var aliasName = $"use-{Guid.NewGuid():N}.JSON.stage";
        CreateFile(fixture, aliasName, []);
        var namesBefore = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var payloadBefore = File.ReadAllBytes(Path.Combine(fixture.ControlPath, owner.RecordName));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Equal(namesBefore, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(payloadBefore, File.ReadAllBytes(Path.Combine(fixture.ControlPath, owner.RecordName)));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_MalformedLaterRecordRefusesBeforeRemovingEarlierValidGraph()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owners = new[]
        {
            await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
                PackageGraphUseSnapshotState.Committed, CancellationToken.None),
            await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
                PackageGraphUseSnapshotState.Committed, CancellationToken.None)
        };
        foreach (var owner in owners)
            await owner.DisposeAsync();

        var malformedOwner = owners.OrderBy(static owner => owner.Record.UseId).Last();
        var recordPath = Path.Combine(fixture.ControlPath, malformedOwner.RecordName);
        var originalIdentity = fixture.Files.InspectChildNoFollow(fixture.Control, malformedOwner.RecordName)!.Identity;
        var malformed = File.ReadAllBytes(recordPath);
        malformed[0] = (byte)'!';
        File.WriteAllBytes(recordPath, malformed);
        Assert.Equal(originalIdentity, fixture.Files.InspectChildNoFollow(fixture.Control, malformedOwner.RecordName)!.Identity);
        var namesBefore = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Equal(namesBefore, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
        foreach (var owner in owners)
        {
            Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.RecordName)));
            Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_RefusesWrongCompleteEpochBeforeChangingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var namesBefore = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(epoch: 5), CancellationToken.None));

        Assert.Equal(namesBefore, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_RefusesSameIdentityStageMutationDiscoveredUnderFreshSentinelToken()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var useId = Guid.NewGuid();
        var sentinelName = $"use-{useId:N}.sentinel";
        var stageName = $"use-{useId:N}.json.stage";
        var originalStage = new byte[] { 0x7b, 0x22, 0x76 };
        var replacementStage = new byte[] { 0x7b, 0x22, 0x78 };
        CreateFile(fixture, sentinelName, []);
        CreateFile(fixture, stageName, originalStage);
        var stageIdentity = fixture.Files.InspectChildNoFollow(fixture.Control, stageName)!.Identity;
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterRemovalToken = name =>
            {
                if (string.Equals(name, sentinelName, StringComparison.Ordinal))
                    File.WriteAllBytes(Path.Combine(fixture.ControlPath, stageName), replacementStage);
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Equal(stageIdentity, fixture.Files.InspectChildNoFollow(fixture.Control, stageName)!.Identity);
        Assert.Equal(replacementStage, File.ReadAllBytes(Path.Combine(fixture.ControlPath, stageName)));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, sentinelName)));
        Assert.Equal(2, fixture.UseArtifactNames().Length);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("reaping")]
    [InlineData("deleting")]
    [InlineData("stage-deleting")]
    public async Task RecoverAsync_AfterJournalMoveFailureRestartsFromPublishedPhase(string phase)
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterRecoveryMove = (_, destination) =>
            {
                if (string.Equals(destination, $"use-{owner.Record.UseId:N}.{phase}", StringComparison.Ordinal))
                    throw new IOException("Injected restart after the journal move took effect.");
            }
        };
        if (phase == "stage-deleting")
        {
            File.Delete(Path.Combine(fixture.ControlPath, owner.RecordName));
            CreateFile(fixture, $"use-{owner.Record.UseId:N}.json.stage", [0x7b, 0x22, 0x76]);
        }

        await Assert.ThrowsAsync<IOException>(() => new PackageGraphUseRecordStore(hooks).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));
        Assert.Contains($"use-{owner.Record.UseId:N}.{phase}", fixture.UseArtifactNames());

        var resumed = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);
        Assert.Contains(owner.Record.UseId, resumed.RecoveredUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_AfterSentinelRemovalFailureResumesRecordOnlyJournal()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterRecoveryRemoval = name =>
            {
                if (string.Equals(name, owner.SentinelName, StringComparison.Ordinal))
                    throw new IOException("Injected restart after sentinel removal took effect.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => new PackageGraphUseRecordStore(hooks).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Equal(new[] { $"use-{owner.Record.UseId:N}.deleting" },
            fixture.UseArtifactNames().Select(static name => name!).ToArray());
        Assert.False(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
        var resumed = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);
        Assert.Contains(owner.Record.UseId, resumed.RecoveredUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_CancellationAfterJournalMoveLeavesResumablePhase()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterRecoveryMove = (_, destination) =>
            {
                if (string.Equals(destination, $"use-{owner.Record.UseId:N}.reaping", StringComparison.Ordinal))
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), cancellation.Token));

        Assert.Equal(new[] { $"use-{owner.Record.UseId:N}.reaping", owner.SentinelName }
                .OrderBy(static name => name, StringComparer.Ordinal),
            fixture.UseArtifactNames().OrderBy(static name => name!, StringComparer.Ordinal));
        var resumed = await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);
        Assert.Equal([owner.Record.UseId], resumed.RecoveredUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_AfterTerminalRecordRemovalFailureIsAnIdempotentRestart()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterRecoveryRemoval = name =>
            {
                if (string.Equals(name, $"use-{owner.Record.UseId:N}.deleting", StringComparison.Ordinal))
                    throw new IOException("Injected restart after terminal record removal took effect.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => new PackageGraphUseRecordStore(hooks).RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Empty(fixture.UseArtifactNames());
        Assert.Empty((await store.RecoverAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None)).RecoveredUseIds);
        Assert.Empty(fixture.UseArtifactNames());
    }

    private static void CreateFile(NativeFixture fixture, string name, byte[] contents)
    {
        using var file = fixture.Files.CreateFileExclusiveAt(fixture.Control, name);
        fixture.Files.WriteNewControlFile(file, contents);
    }

    private static PhysicalStoreEntryInfo MoveArtifact(NativeFixture fixture, string sourceName, string destinationName)
    {
        var source = fixture.Files.InspectChildNoFollow(fixture.Control, sourceName)
            ?? throw new InvalidOperationException($"The fixture source '{sourceName}' is missing.");
        var recovery = fixture.Files as IPhysicalStoreControlRecoveryFileSystem
            ?? throw new InvalidOperationException("The native fixture has no control-recovery provider.");
        return recovery.MoveControlFileNoReplaceAt(fixture.Control, sourceName, source.Identity, destinationName);
    }

    private static async Task RemoveArtifactAsync(NativeFixture fixture, string name)
    {
        var info = fixture.Files.InspectChildNoFollow(fixture.Control, name)
            ?? throw new InvalidOperationException($"The fixture artifact '{name}' is missing.");
        var recovery = fixture.Files as IPhysicalStoreControlRecoveryFileSystem
            ?? throw new InvalidOperationException("The native fixture has no control-recovery provider.");
        var token = await recovery.TryOpenAndLockControlFileForRemovalAt(fixture.Control, name, info.Identity)
            ?? throw new InvalidOperationException($"The fixture artifact '{name}' unexpectedly had a busy lock.");
        await using (token.ConfigureAwait(false))
            await recovery.RemoveLockedControlFileAsync(token).ConfigureAwait(false);
    }
}

public sealed class SupportedPhysicalStoreTheoryAttribute : TheoryAttribute
{
    public SupportedPhysicalStoreTheoryAttribute()
    {
        if (!UnixPhysicalStoreFileSystem.IsSupportedPlatform && !WindowsPhysicalStoreFileSystem.IsSupportedPlatform)
            Skip = "Physical store locking is qualified only on Darwin arm64, Linux x64/arm64, and Windows x64; this skip is not runtime acceptance.";
    }
}
