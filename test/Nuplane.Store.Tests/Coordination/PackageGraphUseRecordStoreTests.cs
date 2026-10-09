using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Feeds;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageGraphUseRecordStoreTests
{
    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesMismatchedHeldRootBeforeCreatingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var actual = fixture.RootIdentity.HandleIdentity;
        var wrongRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            actual.Provider, actual.VolumeOrDeviceId, "not-the-held-root"));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(fixture.Files).PublishAsync(
            fixture.Root, wrongRoot, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_ClassifiesLiveAndStaleCompleteGraphsWithoutChangingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var candidate = fixture.CreateCandidate();
        var pendingOwner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, candidate,
            PackageGraphUseSnapshotState.Pending, CancellationToken.None);
        var committedOwner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, candidate,
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        var originalNames = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        try
        {
            var live = await store.InspectAsync(fixture.Root, fixture.RootIdentity,
                fixture.CreateCompleteBoundary(), CancellationToken.None);

            Assert.Equal(2, live.Entries.Count);
            Assert.All(live.Entries, entry => Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState));
            Assert.Contains(live.Entries, entry => entry.Record.SnapshotState == PackageGraphUseSnapshotState.Pending);
            Assert.Contains(live.Entries, entry => entry.Record.SnapshotState == PackageGraphUseSnapshotState.Committed);
            Assert.All(live.Entries, entry =>
            {
                Assert.Equal(3, entry.Record.GraphSnapshot.Nodes.Count);
                Assert.Equal(2, entry.Record.GraphSnapshot.Edges.Count);
            });
            Assert.Equal(originalNames, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));

            await pendingOwner.DisposeAsync();
            await committedOwner.DisposeAsync();

            var stale = await store.InspectAsync(fixture.Root, fixture.RootIdentity,
                fixture.CreateCompleteBoundary(), CancellationToken.None);

            Assert.Equal(2, stale.Entries.Count);
            Assert.All(stale.Entries, entry => Assert.Equal(GraphUseRecordOwnershipState.Stale, entry.OwnershipState));
            Assert.Equal(originalNames, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
            foreach (var published in new[] { pendingOwner, committedOwner })
            {
                using var sentinel = fixture.Files.OpenFileChildNoFollow(fixture.Control, published.SentinelName, FileAccess.ReadWrite);
                await using var reacquired = await fixture.Files.TryAcquireExclusiveLock(sentinel);
                Assert.NotNull(reacquired);
            }
        }
        finally
        {
            await pendingOwner.DisposeAsync();
            await committedOwner.DisposeAsync();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_EmptyNamespaceReturnsNoUseRecords()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var before = fixture.UseArtifactNames();

        var result = await new PackageGraphUseRecordStore(fixture.Files).InspectAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);

        Assert.Empty(result.Entries);
        Assert.Equal(fixture.RootIdentity, result.RootIdentity);
        Assert.Equal(4L, result.EnrollmentEpoch);
        Assert.Equal(before, fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_CancellationAfterAcquiringProbeReleasesLockAndPreservesArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var publisher = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await publisher.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();

        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterLockAttempt = (_, acquiredLock) =>
            {
                if (acquiredLock is not null)
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).InspectAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), cancellation.Token));

        Assert.Equal(2, fixture.UseArtifactNames().Length);
        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.SentinelName, FileAccess.ReadWrite);
        var retry = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(retry);
        await retry!.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.RecordName)));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_CumulativePayloadBudgetRefusesBeforeProbingNextGraph()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var publisher = new PackageGraphUseRecordStore(fixture.Files);
        var candidate = fixture.CreateCandidate();
        await using var firstOwner = await publisher.PublishAsync(fixture.Root, fixture.RootIdentity, 4, candidate,
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        var completionOpens = 0;
        var recordOpens = 0;
        var hooks = new PublicationHooks(fixture.Files)
        {
            BeforeOpen = (_, name) =>
            {
                if (string.Equals(name, PackageInstallStore.CompletionMarkerFileName, StringComparison.Ordinal))
                    completionOpens++;
                if (name.EndsWith(".json", StringComparison.Ordinal))
                    recordOpens++;
            }
        };
        var baseline = await new PackageGraphUseRecordStore(hooks).InspectAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None);
        Assert.Single(baseline.Entries);
        var oneGraphCompletionOpens = completionOpens;
        Assert.True(oneGraphCompletionOpens > 0);
        var oneGraphRecordOpens = recordOpens;
        Assert.True(oneGraphRecordOpens > 0);
        completionOpens = 0;
        recordOpens = 0;

        await using var secondOwner = await publisher.PublishAsync(fixture.Root, fixture.RootIdentity, 4, candidate,
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);

        var serializer = new GraphUsePayloadSerializer();
        var firstPayloadSize = serializer.Serialize(firstOwner.Record).Length;
        var secondPayloadSize = serializer.Serialize(secondOwner.Record).Length;
        Assert.Equal(firstPayloadSize, secondPayloadSize);
        var combinedLimit = checked((long)firstPayloadSize + secondPayloadSize - 1);
        Assert.True(firstPayloadSize <= combinedLimit);
        Assert.True((long)firstPayloadSize + secondPayloadSize > combinedLimit);
        Assert.True(combinedLimit < PackageGraphUseRecordStore.DefaultMaximumTotalUseRecordBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageGraphUseRecordStore(fixture.Files, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageGraphUseRecordStore(
            fixture.Files, PackageGraphUseRecordStore.DefaultMaximumTotalUseRecordBytes + 1));

        var originalNames = fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks, combinedLimit).InspectAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));

        Assert.Equal(oneGraphCompletionOpens, completionOpens);
        Assert.Equal(oneGraphRecordOpens, recordOpens);
        Assert.Equal(originalNames, fixture.UseArtifactNames().OrderBy(static name => name, StringComparer.Ordinal));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesMissingRecordWithoutDeletingItsSentinel()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        File.Delete(Path.Combine(fixture.ControlPath, owner.RecordName));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesMissingSentinelWithoutDeletingItsRecord()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Pending, CancellationToken.None);
        await owner.DisposeAsync();
        File.Delete(Path.Combine(fixture.ControlPath, owner.SentinelName));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.RecordName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesUnknownStageName()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        using (var stage = fixture.Files.CreateFileExclusiveAt(fixture.Control, $"use-{Guid.NewGuid():N}.json.stage"))
            fixture.Files.WriteNewControlFile(stage, ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesNoncanonicalUsePrefixAndExtension()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        using (var alias = fixture.Files.CreateFileExclusiveAt(fixture.Control, $"USE-{Guid.NewGuid():N}.JSON"))
            fixture.Files.WriteNewControlFile(alias, ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesMalformedRecordAndDigestMismatch()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        var recordPath = Path.Combine(fixture.ControlPath, owner.RecordName);
        var originalPayload = File.ReadAllBytes(recordPath);
        var malformedPayload = originalPayload.ToArray();
        malformedPayload[^1] = (byte)'x';
        File.WriteAllBytes(recordPath, malformedPayload);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));

        var originalJson = Encoding.UTF8.GetString(originalPayload);
        const string digestProperty = "\"payloadDigest\":\"";
        var digestStart = originalJson.IndexOf(digestProperty, StringComparison.Ordinal);
        Assert.True(digestStart >= 0);
        digestStart += digestProperty.Length;
        var replacement = originalJson[digestStart] == '0' ? '1' : '0';
        var digestMismatchJson = originalJson[..digestStart] + replacement + originalJson[(digestStart + 1)..];
        File.WriteAllBytes(recordPath, Encoding.UTF8.GetBytes(digestMismatchJson));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesWhenRecordedInstallCompletionEvidenceIsMissing()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await owner.DisposeAsync();
        File.Delete(Path.Combine(fixture.RootPath, "sample", "Sample.Shared", "2.0.0", PackageInstallStore.CompletionMarkerFileName));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.RecordName)));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, owner.SentinelName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesDigestValidRecordBoundToDifferentSentinelIdentity()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var owner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Pending, CancellationToken.None);
        await owner.DisposeAsync();

        var actualRoot = fixture.RootIdentity.HandleIdentity;
        var differentSentinel = new PhysicalFileIdentity(actualRoot.Provider, actualRoot.VolumeOrDeviceId, "other-sentinel-file");
        var mismatched = GraphUseRecord.Create(fixture.RootIdentity, 4, owner.Record.UseId, owner.Record.GraphSnapshot,
            owner.Record.SnapshotState, differentSentinel, GraphUseLifetimeKind.OsExclusiveSentinel, Environment.ProcessId);
        File.WriteAllBytes(Path.Combine(fixture.ControlPath, owner.RecordName), new GraphUsePayloadSerializer().Serialize(mismatched));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesRecordFromAnotherEnrollmentEpoch()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var epochOwner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Pending, CancellationToken.None);
        await epochOwner.DisposeAsync();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(epoch: 5), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesGraphWithForeignRootWithoutRemovingEitherArtifact()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var foreignRootOwner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4,
            fixture.CreateCandidateWithAdditionalRoot(), PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await foreignRootOwner.DisposeAsync();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, foreignRootOwner.RecordName)));
        Assert.True(File.Exists(Path.Combine(fixture.ControlPath, foreignRootOwner.SentinelName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesHardLinkedSentinel()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);
        var hardLinkOwner = await store.PublishAsync(fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(),
            PackageGraphUseSnapshotState.Committed, CancellationToken.None);
        await hardLinkOwner.DisposeAsync();
        CreateHardLink(Path.Combine(fixture.ControlPath, hardLinkOwner.SentinelName),
            Path.Combine(fixture.RootPath, "graph-use-sentinel-alias"));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.InspectAsync(fixture.Root, fixture.RootIdentity,
            fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectAsync_RefusesWhenBoundedControlEnumerationOverflows()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        for (var index = 0; index <= 4096; index++)
            File.WriteAllBytes(Path.Combine(fixture.ControlPath, $"unrelated-{index:D4}"), []);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(fixture.Files).InspectAsync(
            fixture.Root, fixture.RootIdentity, fixture.CreateCompleteBoundary(), CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_InvalidCandidateMetadataIsRejectedBeforeCreatingArtifacts()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var store = new PackageGraphUseRecordStore(fixture.Files);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 0, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), (PackageGraphUseSnapshotState)99,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());

        var actual = fixture.RootIdentity.HandleIdentity;
        var foreignRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            actual.Provider, actual.VolumeOrDeviceId, "foreign-root"));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(foreignRoot), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));
        Assert.Empty(fixture.UseArtifactNames());
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_ReopensExactRecordAndRetainsNativeSentinelUntilDisposal()
    {
        using var fixture = NativeFixture.Create();
        var candidate = fixture.CreateCandidate();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        string recordName;
        string sentinelName;
        PhysicalFileIdentity recordIdentity;
        PhysicalFileIdentity sentinelIdentity;

        await using (var owner = await new PackageGraphUseRecordStore(fixture.Files).PublishAsync(
                         fixture.Root, fixture.RootIdentity, 4, candidate, PackageGraphUseSnapshotState.Pending,
                         CancellationToken.None))
        {
            recordName = owner.RecordName;
            sentinelName = owner.SentinelName;
            recordIdentity = owner.RecordIdentity;
            sentinelIdentity = owner.SentinelIdentity;
            Assert.Equal($"use-{owner.Record.UseId:N}.json", owner.RecordName);
            Assert.Equal($"use-{owner.Record.UseId:N}.sentinel", owner.SentinelName);
            Assert.Equal(GraphUseLifetimeKind.OsExclusiveSentinel, owner.Record.LifetimeKind);
            Assert.Equal(4L, owner.Record.EnrollmentEpoch);
            Assert.Equal(fixture.RootIdentity, owner.Record.RootIdentity);
            Assert.Equal(owner.Record.SentinelIdentity, owner.SentinelIdentity);
            Assert.Equal(PhysicalStoreEntryKind.RegularFile,
                fixture.Files.InspectChildNoFollow(fixture.Control, owner.RecordName)!.Kind);
            var sentinelInfo = fixture.Files.InspectChildNoFollow(fixture.Control, owner.SentinelName)!;
            Assert.Equal(PhysicalStoreEntryKind.RegularFile, sentinelInfo.Kind);
            Assert.Equal(1UL, sentinelInfo.LinkCount);
            Assert.Equal(0L, sentinelInfo.Length);
            var names = Assert.IsAssignableFrom<IPhysicalStoreNameFileSystem>(fixture.Files);
            Assert.Equal(owner.SentinelName,
                names.ObserveCanonicalFileNameNoFollow(fixture.Control, owner.SentinelName, sentinelIdentity).Basename);
            Assert.Equal(owner.RecordName,
                names.ObserveCanonicalFileNameNoFollow(fixture.Control, owner.RecordName, recordIdentity).Basename);

            using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.SentinelName, FileAccess.ReadWrite);
            Assert.Null(await fixture.Files.TryAcquireExclusiveLock(probe));

            byte[] bytes;
            using (var recordFile = fixture.Files.OpenFileChildNoFollow(fixture.Control, owner.RecordName, FileAccess.Read))
                bytes = fixture.Files.ReadControlFile(recordFile, GraphUsePayloadSerializer.MaximumPayloadBytes);
            var restored = new GraphUsePayloadSerializer().Deserialize(bytes);
            Assert.Equal(owner.Record.PayloadDigest, restored.PayloadDigest);
            Assert.True(owner.Record.GraphSnapshot.HasSamePayloadAs(restored.GraphSnapshot));
            Assert.Equal(bytes.Length, fixture.Files.InspectChildNoFollow(fixture.Control, owner.RecordName)!.Length);
        }

        var record = fixture.Files.InspectChildNoFollow(fixture.Control, recordName);
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, sentinelName);
        Assert.NotNull(record);
        Assert.NotNull(sentinel);
        Assert.Equal(recordIdentity, record.Identity);
        Assert.Equal(sentinelIdentity, sentinel.Identity);
        Assert.Equal(2, fixture.UseArtifactNames().Length);

        using var retry = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var reacquired = await fixture.Files.TryAcquireExclusiveLock(retry);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesWhenNativeSentinelLockIsBusyAndPreservesCreatedFile()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files);
        hooks.AfterExclusiveCreate = (parent, name) =>
        {
            if (!name.EndsWith(".sentinel", StringComparison.Ordinal))
                return;
            hooks.BusySentinelFile = fixture.Files.OpenFileChildNoFollow(parent, name, FileAccess.ReadWrite);
            hooks.BusySentinelLock = fixture.Files.TryAcquireExclusiveLock(hooks.BusySentinelFile)
                .AsTask().GetAwaiter().GetResult();
            Assert.NotNull(hooks.BusySentinelLock);
        };
        var store = new PackageGraphUseRecordStore(hooks);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => store.PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Committed,
            CancellationToken.None));

        try
        {
            Assert.NotNull(hooks.BusySentinelFile);
            Assert.NotNull(hooks.BusySentinelLock);
            var files = fixture.UseArtifactNames();
            Assert.Single(files);
            Assert.EndsWith(".sentinel", files[0]!, StringComparison.Ordinal);
        }
        finally
        {
            if (hooks.BusySentinelLock is not null)
                await hooks.BusySentinelLock.DisposeAsync();
            hooks.BusySentinelFile?.Dispose();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_NoReplaceRaceLeavesUnpublishedStageAndNeverReturnsOwnership()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            BeforePublish = (parent, _, destination) =>
            {
                using var raced = fixture.Files.CreateFileExclusiveAt(parent, destination);
                fixture.Files.WriteNewControlFile(raced, Encoding.UTF8.GetBytes("external"));
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Equal(3, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.Contains(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
        var sentinelName = names.Single(name => name!.EndsWith(".sentinel", StringComparison.Ordinal))!;
        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var lockAfterRefusal = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterRefusal);
        await lockAfterRefusal!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RejectsStagedByteTamperingAndLeavesArtifactsForInspection()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            BeforeOpen = (_, name) =>
            {
                if (!name.EndsWith(".json.stage", StringComparison.Ordinal))
                    return;
                var path = Path.Combine(fixture.ControlPath, name);
                var bytes = File.ReadAllBytes(path);
                bytes[0] = (byte)'!';
                File.WriteAllBytes(path, bytes);
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.DoesNotContain(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_CancellationAfterStageFlushReleasesSentinelAndPreservesEvidence()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterWrite = (_, name) =>
            {
                if (name.EndsWith(".json.stage", StringComparison.Ordinal))
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            cancellation.Token));

        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        Assert.Contains(names, static name => name!.EndsWith(".sentinel", StringComparison.Ordinal));
        Assert.Contains(names, static name => name!.EndsWith(".json.stage", StringComparison.Ordinal));
        Assert.DoesNotContain(names, static name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) && !name.EndsWith(".json.stage", StringComparison.Ordinal));
        var sentinelName = names.Single(name => name!.EndsWith(".sentinel", StringComparison.Ordinal))!;
        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var lockAfterCancellation = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterCancellation);
        await lockAfterCancellation!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_CancellationAfterNativePublicationLeavesVerifiedArtifactsWithoutOwner()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        using var cancellation = new CancellationTokenSource();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterPublish = (_, _, destination) =>
            {
                if (destination.EndsWith(".json", StringComparison.Ordinal))
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            cancellation.Token));

        await AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(fixture);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_ProviderFailureAfterNativePublicationLeavesEvidenceWithoutOwner()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files)
        {
            AfterPublish = static (_, _, destination) =>
            {
                if (destination.EndsWith(".json", StringComparison.Ordinal))
                    throw new IOException("Injected failure after the native publication completed.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Committed,
            CancellationToken.None));

        await AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(fixture);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAsync_RefusesSentinelTamperingDuringExclusiveInitialization()
    {
        using var fixture = NativeFixture.Create();
        await using var callerRootLock = await fixture.HoldRootLockAsync();
        var hooks = new PublicationHooks(fixture.Files);
        hooks.AfterExclusiveCreate = (parent, name) =>
        {
            if (name.EndsWith(".sentinel", StringComparison.Ordinal))
            {
                hooks.SentinelName = name;
                hooks.SentinelIdentity = fixture.Files.InspectChildNoFollow(parent, name)!.Identity;
                using var stream = new FileStream(Path.Combine(fixture.ControlPath, name), FileMode.Open,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.WriteByte(0x5A);
                stream.Flush(flushToDisk: true);
            }
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => new PackageGraphUseRecordStore(hooks).PublishAsync(
            fixture.Root, fixture.RootIdentity, 4, fixture.CreateCandidate(), PackageGraphUseSnapshotState.Pending,
            CancellationToken.None));

        var names = fixture.UseArtifactNames();
        Assert.Single(names);
        Assert.Contains(names, static name => name is not null && name.EndsWith(".sentinel", StringComparison.Ordinal));
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, hooks.SentinelName!);
        Assert.NotNull(sentinel);
        Assert.Equal(hooks.SentinelIdentity, sentinel.Identity);
        Assert.Equal(1L, sentinel.Length);

        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, hooks.SentinelName!, FileAccess.ReadWrite);
        var lockAfterRefusal = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(lockAfterRefusal);
        await lockAfterRefusal!.DisposeAsync();
    }

    private static async Task AssertPublishedArtifactsRemainAndSentinelIsUnlockedAsync(NativeFixture fixture)
    {
        var names = fixture.UseArtifactNames();
        Assert.Equal(2, names.Length);
        var recordName = names.Single(name => name is not null && name.EndsWith(".json", StringComparison.Ordinal) &&
            !name.EndsWith(".json.stage", StringComparison.Ordinal))!;
        var sentinelName = names.Single(name => name is not null && name.EndsWith(".sentinel", StringComparison.Ordinal))!;
        byte[] payload;
        using (var recordFile = fixture.Files.OpenFileChildNoFollow(fixture.Control, recordName, FileAccess.Read))
            payload = fixture.Files.ReadControlFile(recordFile, GraphUsePayloadSerializer.MaximumPayloadBytes);
        var record = new GraphUsePayloadSerializer().Deserialize(payload);
        Assert.Equal($"use-{record.UseId:N}.json", recordName);
        Assert.Equal($"use-{record.UseId:N}.sentinel", sentinelName);
        var sentinel = fixture.Files.InspectChildNoFollow(fixture.Control, sentinelName);
        Assert.NotNull(sentinel);
        Assert.Equal(record.SentinelIdentity, sentinel.Identity);
        Assert.Equal(0L, sentinel.Length);

        using var probe = fixture.Files.OpenFileChildNoFollow(fixture.Control, sentinelName, FileAccess.ReadWrite);
        var retry = await fixture.Files.TryAcquireExclusiveLock(probe);
        Assert.NotNull(retry);
        await retry!.DisposeAsync();
    }

    private static IPhysicalStoreFileSystem CreateNativeFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private sealed class NativeFixture : IDisposable
    {
        private NativeFixture(
            PackageStoreFixture owner,
            IPhysicalStoreFileSystem files,
            PhysicalStoreDirectoryHandle root,
            PhysicalStoreDirectoryHandle control,
            PhysicalRootIdentity rootIdentity,
            string controlPath,
            PackageInstallIdentity rootAInstall,
            PackageInstallIdentity rootBInstall,
            PackageInstallIdentity sharedInstall)
        {
            Owner = owner;
            Files = files;
            Root = root;
            Control = control;
            RootIdentity = rootIdentity;
            ControlPath = controlPath;
            RootAInstall = rootAInstall;
            RootBInstall = rootBInstall;
            SharedInstall = sharedInstall;
        }

        private PackageStoreFixture Owner { get; }
        internal IPhysicalStoreFileSystem Files { get; }
        internal PhysicalStoreDirectoryHandle Root { get; }
        internal PhysicalStoreDirectoryHandle Control { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal string ControlPath { get; }
        internal string RootPath => Owner.PackageInstallRoot;
        private PackageInstallIdentity RootAInstall { get; }
        private PackageInstallIdentity RootBInstall { get; }
        private PackageInstallIdentity SharedInstall { get; }

        internal static NativeFixture Create()
        {
            var owner = new PackageStoreFixture();
            var files = CreateNativeFileSystem();
            PhysicalStoreDirectoryHandle? root = null;
            PhysicalStoreDirectoryHandle? control = null;
            try
            {
                root = PhysicalStoreTestDirectory.Open(files, owner.PackageInstallRoot);
                var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(root).Identity);
                var controlPath = owner.CreateDirectory("packages/.nuplane-store");
                control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
                using (var rootLock = files.CreateFileExclusiveAt(control, "root.lock"))
                    files.WriteNewControlFile(rootLock, ReadOnlyMemory<byte>.Empty);
                foreach (var path in new[]
                         {
                             "sample/Sample.RootA/1.0.0",
                             "sample/Sample.RootB/1.0.0",
                             "sample/Sample.Shared/2.0.0"
                         })
                {
                    var installPath = owner.CreateDirectory($"packages/{path}");
                    File.WriteAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName), []);
                }

                var reader = new PackageInstallIdentityReader(files);
                using var rootA = reader.Observe(root, rootIdentity, "sample/Sample.RootA/1.0.0", "Sample.RootA", "1.0.0");
                using var rootB = reader.Observe(root, rootIdentity, "sample/Sample.RootB/1.0.0", "Sample.RootB", "1.0.0");
                using var shared = reader.Observe(root, rootIdentity, "sample/Sample.Shared/2.0.0", "Sample.Shared", "2.0.0");
                var result = new NativeFixture(owner, files, root, control, rootIdentity, controlPath,
                    rootA.InstallIdentity, rootB.InstallIdentity, shared.InstallIdentity);
                root = null;
                control = null;
                return result;
            }
            catch
            {
                control?.Dispose();
                root?.Dispose();
                owner.Dispose();
                throw;
            }
        }

        internal ProtectedGraphSnapshot CreateCandidate(PhysicalRootIdentity? rootOverride = null)
        {
            var graphRoot = rootOverride ?? RootIdentity;
            var rootAId = Guid.NewGuid();
            var rootBId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var rootARequest = new PackageRequest("Sample.RootA", "[1.0.0,2.0.0)", null,
                PackageUpdatePolicy.Range, "graph-use-test");
            var rootBRequest = new PackageRequest("Sample.RootB", "[1.0.0,2.0.0)", null,
                PackageUpdatePolicy.Range, "graph-use-test");
            var nodes = new[]
            {
                Internal<PackageGraphNodeIdentity>(rootAId, CopyInstall(graphRoot, RootAInstall)),
                Internal<PackageGraphNodeIdentity>(rootBId, CopyInstall(graphRoot, RootBInstall)),
                Internal<PackageGraphNodeIdentity>(sharedId, CopyInstall(graphRoot, SharedInstall))
            };
            var requests = new[]
            {
                Internal<PackageGraphRootSelection>(rootARequest, rootAId),
                Internal<PackageGraphRootSelection>(rootBRequest, rootBId)
            };
            var edges = new[]
            {
                Internal<PackageGraphEdgeIdentity>(rootAId, sharedId, "Sample.Shared", "[2.0.0,)", string.Empty, false),
                Internal<PackageGraphEdgeIdentity>(rootBId, sharedId, "Sample.Shared", "[2.0.0,)", string.Empty, false)
            };
            var candidate = new ProtectedGraphSnapshot(Guid.NewGuid(), "graph-use-test", Guid.NewGuid().ToString("N"),
                ProtectedGraphDisposition.Active, [graphRoot], requests, nodes, edges, recoverySelectionEvidence: null);
            return candidate;
        }

        internal ProtectedGraphSnapshot CreateCandidateWithAdditionalRoot()
        {
            var candidate = CreateCandidate();
            var identity = RootIdentity.HandleIdentity;
            var foreignRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
                identity.Provider, identity.VolumeOrDeviceId, "other-physical-root"));
            var foreignInstall = new PackageInstallIdentity(foreignRoot, "Sample.Foreign", "3.0.0",
                "sample/Sample.Foreign/3.0.0", RootAInstall.DirectoryIdentity, "foreign-completion-v1");
            var foreignNodeId = Guid.NewGuid();
            var foreignRequest = new PackageRequest("Sample.Foreign", "[3.0.0,4.0.0)", null,
                PackageUpdatePolicy.Range, "graph-use-test");
            return new ProtectedGraphSnapshot(candidate.SnapshotId, candidate.GraphId, candidate.GenerationId,
                ProtectedGraphDisposition.Active, [RootIdentity, foreignRoot],
                candidate.RequestedRoots.Append(Internal<PackageGraphRootSelection>(foreignRequest, foreignNodeId)),
                candidate.Nodes.Append(Internal<PackageGraphNodeIdentity>(foreignNodeId, foreignInstall)),
                candidate.Edges, recoverySelectionEvidence: null);
        }

        // Structural input only; production callers must independently verify the persisted Complete membership and all member states.
        internal RootMembershipRecord CreateCompleteBoundary(long epoch = 4)
            => new(RootMembershipRecord.CurrentSchemaVersion, RootIdentity, epoch, RootMembershipStatus.Complete,
                [], [], [], null, new string('0', 64));

        private static PackageInstallIdentity CopyInstall(PhysicalRootIdentity root, PackageInstallIdentity install)
            => new(root, install.PackageId, install.Version, install.RootRelativeInstallPath,
                install.DirectoryIdentity, install.CompletionIdentity, install.VerifiedArchiveHash);

        internal async Task<IAsyncDisposable> HoldRootLockAsync()
        {
            var file = Files.OpenFileChildNoFollow(Control, "root.lock", FileAccess.ReadWrite);
            try
            {
                var nativeLock = await Files.TryAcquireExclusiveLock(file).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The fixture could not acquire its caller-owned root lock.");
                return new NativeLockOwnership(file, nativeLock);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal string?[] UseArtifactNames()
            => Directory.GetFiles(ControlPath).Select(Path.GetFileName)
                .Where(static name => name is not null && name.StartsWith("use-", StringComparison.Ordinal))
                .ToArray();

        public void Dispose()
        {
            Control.Dispose();
            Root.Dispose();
            Owner.Dispose();
        }

        private static T Internal<T>(params object?[] arguments)
            => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;

        private sealed class NativeLockOwnership(PhysicalStoreFileHandle file, IAsyncDisposable nativeLock) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                try { await nativeLock.DisposeAsync().ConfigureAwait(false); }
                finally { file.Dispose(); }
            }
        }
    }

    internal sealed class PublicationHooks : IPhysicalStoreFileSystem, IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem, IPhysicalStoreDirectoryPublicationFileSystem,
        IPhysicalStoreDirectoryEnumerationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStorePublicationFileSystem _publication;
        private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryPublication;
        private readonly IPhysicalStoreDirectoryEnumerationFileSystem _enumeration;
        private readonly Dictionary<PhysicalFileIdentity, string> _createdNames = [];

        internal PublicationHooks(IPhysicalStoreFileSystem files)
        {
            _files = files;
            _names = files as IPhysicalStoreNameFileSystem ?? throw new InvalidOperationException();
            _publication = files as IPhysicalStorePublicationFileSystem ?? throw new InvalidOperationException();
            _directoryPublication = files as IPhysicalStoreDirectoryPublicationFileSystem ?? throw new InvalidOperationException();
            _enumeration = files as IPhysicalStoreDirectoryEnumerationFileSystem ?? throw new InvalidOperationException();
        }

        internal Action<PhysicalStoreDirectoryHandle, string>? AfterExclusiveCreate { get; set; }
        internal Action<PhysicalStoreFileHandle, string>? AfterWrite { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string>? BeforeOpen { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? BeforePublish { get; set; }
        internal PhysicalStoreFileHandle? BusySentinelFile { get; set; }
        internal IAsyncDisposable? BusySentinelLock { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? AfterPublish { get; set; }
        internal Action<PhysicalStoreFileHandle, IAsyncDisposable?>? AfterLockAttempt { get; set; }
        internal string? SentinelName { get; set; }
        internal PhysicalFileIdentity? SentinelIdentity { get; set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _files.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => _files.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
        {
            BeforeOpen?.Invoke(parent, singleName);
            return _files.OpenFileChildNoFollow(parent, singleName, access);
        }
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => _files.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => _files.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var file = _files.CreateFileExclusiveAt(parent, singleName);
            _createdNames[_files.InspectHandle(file).Identity] = singleName;
            AfterExclusiveCreate?.Invoke(parent, singleName);
            return file;
        }
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes) => _files.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
        {
            _files.WriteNewControlFile(file, contents);
            var identity = _files.InspectHandle(file).Identity;
            if (_createdNames.TryGetValue(identity, out var name))
                AfterWrite?.Invoke(file, name);
        }
        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var ownership = await _files.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            AfterLockAttempt?.Invoke(file, ownership);
            return ownership;
        }
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
        {
            BeforePublish?.Invoke(parent, stagedName, destinationName);
            var published = _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
            AfterPublish?.Invoke(parent, stagedName, destinationName);
            return published;
        }
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent,
            string singleName, PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryPublication.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => _directoryPublication.PublishDirectoryNoReplaceAt(parent, stagedName, expectedStagedIdentity, destinationName);

        public IReadOnlyList<string> EnumerateChildNamesNoFollow(PhysicalStoreDirectoryHandle parent, int maximumEntries)
            => _enumeration.EnumerateChildNamesNoFollow(parent, maximumEntries);
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var error = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : CreateHardLinkUnix(existingPath, newPath) == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (error != 0)
            throw new IOException($"The owned hard-link fixture could not be created (native error {error}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);
}
