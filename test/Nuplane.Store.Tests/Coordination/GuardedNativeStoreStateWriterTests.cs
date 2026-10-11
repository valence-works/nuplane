using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class GuardedNativeStoreStateWriterTests
{
    private const string StateName = "store-state.json";

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_CreatesAndReplacesStateWhileOpenDeleteSharingReaderKeepsOldRecord()
    {
        using var context = CreateContext();
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        var prior = State("1.0.0", 1);

        var created = await writer.WriteAsync(context.Parent, context.Slot, prior, NoReplay, CancellationToken.None);
        Assert.Equal(created.StateIdentity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
        var oldBytes = File.ReadAllBytes(context.StatePath);
        using var oldReader = new FileStream(
            context.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var next = State("2.0.0", 2);
        var replaced = await writer.WriteAsync(context.Parent, context.Slot, next, NoReplay, CancellationToken.None);

        Assert.NotEqual(created.StateIdentity, replaced.StateIdentity);
        using var observedOldRecord = new MemoryStream();
        oldReader.CopyTo(observedOldRecord);
        Assert.Equal(oldBytes, observedOldRecord.ToArray());
        var newlyOpened = await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None);
        Assert.Equal("2.0.0", newlyOpened.ActiveVersionById["package"]);
        Assert.Equal(next.UpdatedAt, newlyOpened.UpdatedAt);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_UsesUnboundedHeldParentStreamsForPayloadsLargerThanFourMiB()
    {
        using var context = CreateContext();
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        var largeValue = new string('x', 5 * 1024 * 1024);
        var state = State(largeValue, 3);

        await writer.WriteAsync(context.Parent, context.Slot, state, NoReplay, CancellationToken.None);

        Assert.True(new FileInfo(context.StatePath).Length > 4L * 1024 * 1024);
        Assert.Equal(long.MaxValue, context.Files.MaximumReadBytes);
        Assert.Equal(long.MaxValue, context.Files.MaximumWriteBytes);
        var loaded = await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None);
        Assert.Equal(largeValue, loaded.ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesPermanentMarkerAndGuardContentionBeforePayloadCodec()
    {
        using (var context = CreateContext())
        {
            await SeedAsync(context, State("1.0.0", 1));
            await using (var setup = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
                             .AcquireAsync(context.Parent, context.Slot, CancellationToken.None))
            {
            }
            File.WriteAllBytes(context.MarkerPath, [1]);
            var serializer = new TrackingPayloadSerializer();
            var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

            Assert.Equal(0, serializer.ReadCalls);
            Assert.Equal(0, serializer.WriteCalls);
        }

        using (var context = CreateContext())
        {
            await SeedAsync(context, State("1.0.0", 1));
            await using var owner = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
                .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
            var serializer = new TrackingPayloadSerializer();
            var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            Assert.Equal(0, serializer.ReadCalls);
            Assert.Equal(0, serializer.WriteCalls);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_ReplaysMarkerAfterAwaitedCallbackAndRefusesMutationBeforeReadingPrior()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        var callbackCalls = 0;

        async ValueTask Replay(CancellationToken _)
        {
            if (Interlocked.Increment(ref callbackCalls) == 2)
            {
                await Task.Yield();
                File.WriteAllBytes(context.MarkerPath, [1]);
            }
        }

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), Replay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(2, callbackCalls);
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Equal(0, serializer.WriteCalls);
        Assert.Equal("1.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesForeignParentAndChangedNameProfileBeforePayloadCodec()
    {
        using var context = CreateContext();
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        var wrongProfile = new StateSlotIdentity(
            context.Slot.ParentIdentity,
            new PhysicalStoreNameSemantics(
                context.Slot.NameSemantics.ProfileId + "-changed",
                context.Slot.NameSemantics.Encoding,
                context.Slot.NameSemantics.CaseSensitive,
                context.Slot.NameSemantics.NormalizationInsensitive),
            context.Slot.CanonicalBasename);
        var profileError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, wrongProfile, State("1.0.0", 1), NoReplay, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, profileError.Reason);

        var otherPath = context.Fixture.CreateDirectory("other-parent");
        using var otherParent = PhysicalStoreTestDirectory.Open(context.NativeFiles, otherPath);
        var foreignError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(otherParent, context.Slot, State("1.0.0", 1), NoReplay, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, foreignError.Reason);
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Equal(0, serializer.WriteCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_SerializationFailureAndCancellationLeavePriorReadableAndReleaseGuard()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var context = CreateContext();
            var priorBytes = await SeedWithUnknownFieldAsync(context);
            using var cancellation = new CancellationTokenSource();
            var serializer = new TrackingPayloadSerializer
            {
                WriteOverride = async (stream, _, token) =>
                {
                    if (cancel)
                    {
                        cancellation.Cancel();
                        await Task.FromCanceled(cancellation.Token);
                    }
                    await stream.WriteAsync("partial"u8.ToArray(), token);
                    throw new IOException("injected payload serialization failure");
                }
            };
            var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(
                    () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));
            }

            Assert.Equal(priorBytes, File.ReadAllBytes(context.StatePath));
            Assert.Equal("1.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
            Assert.DoesNotContain(Directory.GetFiles(context.StateDirectory), path =>
                path.EndsWith(".tmp", StringComparison.Ordinal) || path.EndsWith(".bak", StringComparison.Ordinal));
            await using var nextWriter = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
                .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_BeforeTransitionFailurePreservesPriorAndBackupOriginalUnknownJsonBytes()
    {
        using var context = CreateContext();
        var priorBytes = await SeedWithUnknownFieldAsync(context);
        var injected = new IOException("injected before native transition");
        context.Files.BeforePublish = (_, _, destination) =>
        {
            if (destination != StateName)
                return;
            var backup = Directory.GetFiles(context.StateDirectory)
                .Single(path => path.EndsWith(".bak", StringComparison.Ordinal));
            Assert.Equal(priorBytes, File.ReadAllBytes(backup));
            throw injected;
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var error = await Assert.ThrowsAsync<IOException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Same(injected, error);
        Assert.Equal(priorBytes, File.ReadAllBytes(context.StatePath));
        Assert.DoesNotContain(Directory.GetFiles(context.StateDirectory), path =>
            path.EndsWith(".tmp", StringComparison.Ordinal) || path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("1.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_PreservesCreatedArtifactWhenItsHandleIdentityCannotBeInspected()
    {
        using var context = CreateContext();
        context.Files.AfterCreateFile = (_, name, handle) =>
        {
            if (name.EndsWith(".tmp", StringComparison.Ordinal))
                context.Files.FailInspectFor = handle;
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("1.0.0", 1), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Contains("no verified handle identity", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        await using var nextWriter = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
            .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesMarkerMutationAfterFailedStageCleanup()
    {
        using var context = CreateContext();
        context.Files.BeforeRemove = (_, name, _) =>
        {
            if (name.EndsWith(".tmp", StringComparison.Ordinal))
                File.WriteAllBytes(context.MarkerPath, [1]);
        };
        var serializer = new TrackingPayloadSerializer
        {
            WriteOverride = (_, _, _) => Task.FromException(new IOException("injected serialization failure"))
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("1.0.0", 1), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.True(File.Exists(context.MarkerPath));
        Assert.DoesNotContain(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_PreservesBackupWhenSameIdentityPriorBytesChangeDuringBackupCopy()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        var prior = context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!;
        context.Files.BeforeWriteStream = (_, name) =>
        {
            if (name.EndsWith(".bak", StringComparison.Ordinal))
                File.WriteAllBytes(context.StatePath, PayloadBytes(State("9.0.0", 9)));
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(prior.Identity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("9.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_PreservesStageAndBackupWhenDestinationChangesDuringFailedSerialization()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        var serializer = new TrackingPayloadSerializer
        {
            WriteOverride = async (_, _, _) =>
            {
                File.Delete(context.StatePath);
                File.WriteAllBytes(context.StatePath, PayloadBytes(State("3.0.0", 3)));
                await Task.Yield();
                throw new IOException("injected stage failure after destination replacement");
            }
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("3.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesPublicationWhenSuccessfulStageCodecChangesSameIdentityPriorBytes()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        var prior = context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!;
        var codecCalls = 0;
        var serializer = new TrackingPayloadSerializer
        {
            WriteOverride = async (stream, state, token) =>
            {
                if (Interlocked.Increment(ref codecCalls) == 1)
                    File.WriteAllBytes(context.StatePath, PayloadBytes(State("9.0.0", 9)));
                await new StoreStateSerializer().WritePayloadAsync(stream, state, token);
            }
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Equal(prior.Identity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("9.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_AfterTransitionExceptionAndCancellationReturnVerifiedCommittedOutcome()
    {
        using (var context = CreateContext())
        {
            await SeedAsync(context, State("1.0.0", 1));
            context.Files.AfterPublish = (_, _, destination) =>
            {
                if (destination == StateName)
                    throw new IOException("injected after native transition");
            };
            var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

            var outcome = await writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None);

            Assert.False(outcome.RecoveryArtifactsRemain);
            Assert.Equal(outcome.StateIdentity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
            Assert.Equal("2.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
        }

        using (var context = CreateContext())
        {
            await SeedAsync(context, State("1.0.0", 1));
            using var cancellation = new CancellationTokenSource();
            context.Files.AfterPublish = (_, _, destination) =>
            {
                if (destination == StateName)
                    cancellation.Cancel();
            };
            var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

            var outcome = await writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, cancellation.Token);

            Assert.Equal(outcome.StateIdentity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
            Assert.Equal("2.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_AfterTransitionAmbiguityPreservesEvidenceAndDoesNotSwallowPublisherError()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        var injected = new IOException("injected after transition before verification");
        var unexpectedPath = Path.Combine(context.StateDirectory, "unverified-state.json");
        context.Files.AfterPublish = (_, _, destination) =>
        {
            if (destination != StateName)
                return;
            File.Move(context.StatePath, unexpectedPath);
            File.WriteAllBytes(context.StatePath, PayloadBytes(State("3.0.0", 3)));
            throw injected;
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
        Assert.Contains("injected after transition before verification", error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(unexpectedPath));
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("3.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_CommittedBackupCleanupIoFailureReturnsVerifiedRecoveryOutcome()
    {
        using var context = CreateContext();
        await SeedAsync(context, State("1.0.0", 1));
        context.Files.BeforeRemove = (_, name, _) =>
        {
            if (name.EndsWith(".bak", StringComparison.Ordinal))
                throw new IOException("injected backup cleanup failure");
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var outcome = await writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None);

        Assert.True(outcome.RecoveryArtifactsRemain);
        Assert.Contains(Directory.GetFiles(context.StateDirectory), path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Equal("2.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_PreservesVerifiedCommittedOutcomeWhenGuardReleaseReportsFailure()
    {
        using var context = CreateContext();
        context.Files.FailNextLockRelease = true;
        var writer = new GuardedNativeStoreStateWriter(context.Files, new TrackingPayloadSerializer());

        var outcome = await writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None);

        Assert.NotNull(outcome.GuardReleaseError);
        Assert.Contains("injected native lock release failure", outcome.GuardReleaseError!.ToString(), StringComparison.Ordinal);
        Assert.Equal(outcome.StateIdentity, context.NativeFiles.InspectChildNoFollow(context.Parent, StateName)!.Identity);
        Assert.Equal("2.0.0", (await new StoreStateSerializer().LoadAsync(context.StatePath, CancellationToken.None)).ActiveVersionById["package"]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesAnIncomingOrExistingV2Bundle()
    {
        using (var context = CreateContext())
        {
            var serializer = new TrackingPayloadSerializer();
            var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => writer.WriteAsync(context.Parent, context.Slot, BundleState(), NoReplay, CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
            Assert.Equal(0, serializer.ReadCalls);
            Assert.Equal(0, serializer.WriteCalls);
        }

        using (var context = CreateContext())
        {
            await SeedAsync(context, BundleState());
            var serializer = new TrackingPayloadSerializer();
            var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => writer.WriteAsync(context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
            Assert.Equal(0, serializer.WriteCalls);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesIncomingV1ProtectionBeforeNativeOrPayloadIo()
    {
        using var context = CreateContext();
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        context.Files.FailInspectFor = context.Parent;
        var replayCalls = 0;

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => writer.WriteAsync(
            context.Parent, context.Slot, WithV1Protection(State("owned", 1)),
            _ => { replayCalls++; return ValueTask.CompletedTask; }, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.Equal(0, replayCalls);
        Assert.Equal(0, serializer.ReadCalls);
        Assert.Equal(0, serializer.WriteCalls);
        Assert.Same(context.Parent, context.Files.FailInspectFor);
        Assert.False(File.Exists(context.StatePath));
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesExistingV1ProtectionBeforeStagingAndReleasesGuard()
    {
        using var context = CreateContext();
        await SeedAsync(context, WithV1Protection(State("owned", 1)));
        var priorBytes = File.ReadAllBytes(context.StatePath);
        var serializer = new TrackingPayloadSerializer();
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => writer.WriteAsync(
            context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.True(serializer.ReadCalls > 0);
        Assert.Equal(0, serializer.WriteCalls);
        Assert.Equal(priorBytes, File.ReadAllBytes(context.StatePath));
        AssertNoStateArtifacts(context);
        await using var nextWriter = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
            .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task WriteAsync_RefusesV1ProtectionIntroducedByStageCodecBeforePublication()
    {
        using var context = CreateContext();
        var priorBytes = await SeedWithUnknownFieldAsync(context);
        var serializer = new TrackingPayloadSerializer
        {
            WriteOverride = (stream, state, token) => new StoreStateSerializer()
                .WritePayloadAsync(stream, WithV1Protection(state), token)
        };
        var writer = new GuardedNativeStoreStateWriter(context.Files, serializer);
        var publicationAttempted = false;
        context.Files.BeforePublish = (_, _, _) => publicationAttempted = true;

        var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => writer.WriteAsync(
            context.Parent, context.Slot, State("2.0.0", 2), NoReplay, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, error.Reason);
        Assert.False(publicationAttempted);
        Assert.Equal(priorBytes, File.ReadAllBytes(context.StatePath));
        AssertNoStateArtifacts(context);
        await using var nextWriter = await new PhysicalStoreStateSlotWriteGuard(context.NativeFiles)
            .AcquireAsync(context.Parent, context.Slot, CancellationToken.None);
    }

    private static void AssertNoStateArtifacts(TestContext context)
        => Assert.DoesNotContain(Directory.GetFiles(context.StateDirectory), path =>
            path.EndsWith(".tmp", StringComparison.Ordinal) || path.EndsWith(".bak", StringComparison.Ordinal));

    private static StoreStateRecord WithV1Protection(StoreStateRecord state)
    {
        var unknown = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Unknown,
            PackageProtectionUnknownReasonCode.LegacyProtectionMissing, graphs: null);
        var root = new PhysicalRootIdentity(new PhysicalFileIdentity("test-provider", "test-volume", "test-root"));
        var candidate = new PackageProtectionRecord(1, root, 1, "test-member", 1,
            ProtectionDigest.StateBody(state), new string('0', 64), unknown, unknown, [], true);
        var protection = new PackageProtectionRecord(1, root, 1, "test-member", 1,
            candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), unknown, unknown, [], true);
        return state with { ProtectionRecord = protection };
    }

    private static TestContext CreateContext()
    {
        var fixture = new PackageStoreFixture();
        IPhysicalStoreFileSystem nativeFiles = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        try
        {
            using var root = PhysicalStoreTestDirectory.Open(nativeFiles, fixture.RootPath);
            var parent = nativeFiles.OpenDirectoryChildNoFollow(root, "state");
            var parentInfo = nativeFiles.InspectHandle(parent);
            var semantics = ((IPhysicalStoreNameFileSystem)nativeFiles).ObserveDirectoryNameSemantics(parent);
            var slot = new StateSlotIdentity(parentInfo.Identity, semantics, StateName);
            return new TestContext(fixture, nativeFiles, new NativeHooks(nativeFiles), parent, slot);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static StoreStateRecord State(string version, int timestamp)
        => StoreStateRecord.Empty() with
        {
            ActiveVersionById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["package"] = version
            },
            UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(timestamp)
        };

    private static StoreStateRecord BundleState()
    {
        var state = State("bundle", 1);
        var bodyDigest = ProtectionDigest.StateBody(state);
        var unknown = new PackageProtectionClosureV2(
            PackageProtectionClosureKnowledge.Unknown,
            PackageProtectionUnknownReasonCode.LegacyProtectionMissing,
            graphs: null);
        var row = new PackageProtectionBundleRootRow(
            new PhysicalRootIdentity(new PhysicalFileIdentity("test-provider", "test-volume", "test-root")),
            enrollmentEpoch: 1,
            memberId: "test-member",
            revision: 1,
            stateGeneration: 1,
            stateBodyDigest: bodyDigest,
            activeClosure: unknown,
            recoverableClosure: unknown,
            retiredGraphs: [],
            legacyUnknownRecovery: true);
        var bundle = new PackageProtectionBundle(
            PackageProtectionBundle.CurrentSchemaVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            stateGeneration: 1,
            stateBodyDigest: bodyDigest,
            rows: [row]);
        return state with { ProtectionBundle = bundle };
    }

    private static readonly Func<CancellationToken, ValueTask> NoReplay = static _ => ValueTask.CompletedTask;

    private static async Task SeedAsync(TestContext context, StoreStateRecord state)
        => await File.WriteAllBytesAsync(context.StatePath, PayloadBytes(state));

    private static async Task<byte[]> SeedWithUnknownFieldAsync(TestContext context)
    {
        var raw = Encoding.UTF8.GetBytes("""
            {
              "activeVersionById": { "package": "1.0.0" },
              "lastKnownGoodById": {},
              "lastFailureById": {},
              "lastSuccessfulSourceSnapshots": {},
              "updatedAt": "1970-01-01T00:00:01+00:00",
              "futureField": { "opaque": "preserve in the backup" }
            }
            """);
        await File.WriteAllBytesAsync(context.StatePath, raw);
        return raw;
    }

    private static byte[] PayloadBytes(StoreStateRecord state)
    {
        using var stream = new MemoryStream();
        new StoreStateSerializer().WritePayloadAsync(stream, state, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private sealed class TestContext(
        PackageStoreFixture fixture,
        IPhysicalStoreFileSystem nativeFiles,
        NativeHooks files,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot) : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = fixture;
        internal IPhysicalStoreFileSystem NativeFiles { get; } = nativeFiles;
        internal NativeHooks Files { get; } = files;
        internal PhysicalStoreDirectoryHandle Parent { get; } = parent;
        internal StateSlotIdentity Slot { get; } = slot;
        internal string StatePath => Fixture.StateFilePath;
        internal string StateDirectory => Path.GetDirectoryName(StatePath)!;
        internal string MarkerPath => Fixture.GetPath(
            $"state/{PhysicalStoreStateSlotWriteGuard.ControlDirectoryName}/{Slot.CanonicalBasename}/{PhysicalStoreStateSlotWriteGuard.GroupMarkerLeafName}");

        public void Dispose()
        {
            try { Parent.Dispose(); }
            finally { Fixture.Dispose(); }
        }
    }

    private sealed class TrackingPayloadSerializer : IPackageProtectionBundleStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();

        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal Func<Stream, StoreStateRecord, CancellationToken, Task>? WriteOverride { get; init; }

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return await _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return WriteOverride is null
                ? _inner.WritePayloadAsync(payload, state, cancellationToken)
                : WriteOverride(payload, state, cancellationToken);
        }
    }

    private sealed class NativeHooks :
        IPhysicalStoreFileSystem,
        IPhysicalStoreNameFileSystem,
        IPhysicalStoreDirectoryNameFileSystem,
        IPhysicalStorePackageStreamFileSystem,
        IPhysicalStorePublicationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _files;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStoreDirectoryNameFileSystem _directoryNames;
        private readonly IPhysicalStorePackageStreamFileSystem _streams;
        private readonly IPhysicalStorePublicationFileSystem _publication;

        internal NativeHooks(IPhysicalStoreFileSystem files)
        {
            _files = files;
            _names = (IPhysicalStoreNameFileSystem)files;
            _directoryNames = (IPhysicalStoreDirectoryNameFileSystem)files;
            _streams = (IPhysicalStorePackageStreamFileSystem)files;
            _publication = (IPhysicalStorePublicationFileSystem)files;
        }

        internal Action<PhysicalStoreDirectoryHandle, string, string>? BeforePublish { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, string>? AfterPublish { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, PhysicalFileIdentity>? BeforeRemove { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string, PhysicalStoreFileHandle>? AfterCreateFile { get; set; }
        internal Action<PhysicalStoreDirectoryHandle, string>? BeforeWriteStream { get; set; }
        internal PhysicalStoreHandle? FailInspectFor { get; set; }
        internal bool FailNextLockRelease { get; set; }
        internal long MaximumReadBytes { get; private set; }
        internal long MaximumWriteBytes { get; private set; }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _files.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.InspectChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.OpenDirectoryChildNoFollow(parent, singleName);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory)
            => _files.OpenParentDirectory(directory);
        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, FileAccess access)
            => _files.OpenFileChildNoFollow(parent, singleName, access);
        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedLinkIdentity)
            => _files.ReadLinkTargetNoFollow(parent, singleName, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle)
        {
            if (ReferenceEquals(handle, FailInspectFor))
            {
                FailInspectFor = null;
                throw new IOException("injected native handle inspection failure");
            }

            return _files.InspectHandle(handle);
        }
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
            => _files.CreateDirectoryExclusiveAt(parent, singleName);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string singleName)
        {
            var file = _files.CreateFileExclusiveAt(parent, singleName);
            AfterCreateFile?.Invoke(parent, singleName, file);
            return file;
        }
        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes) => _files.ReadControlFile(file, maximumBytes);
        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => _files.WriteNewControlFile(file, contents);
        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var acquired = await _files.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            if (acquired is null || !FailNextLockRelease)
                return acquired;

            FailNextLockRelease = false;
            return new ThrowingRelease(acquired);
        }
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, singleName, expectedFileIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent, string singleName,
            PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryNames.ObserveCanonicalDirectoryNameNoFollow(parent, singleName, expectedDirectoryIdentity);
        public Stream OpenPackageArchiveReadStream(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent, PhysicalStoreEntryInfo expectedFile, long maximumBytes)
        {
            MaximumReadBytes = maximumBytes;
            return _streams.OpenPackageArchiveReadStream(parent, singleName, file, expectedParent, expectedFile, maximumBytes);
        }
        public Stream CreatePackageFileWriteStream(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalStoreFileHandle file,
            PhysicalStoreEntryInfo expectedParent, long maximumBytes)
        {
            MaximumWriteBytes = maximumBytes;
            BeforeWriteStream?.Invoke(parent, singleName);
            return _streams.CreatePackageFileWriteStream(parent, singleName, file, expectedParent, maximumBytes);
        }
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName,
            PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
        {
            BeforePublish?.Invoke(parent, stagedName, destinationName);
            var result = _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
            AfterPublish?.Invoke(parent, stagedName, destinationName);
            return result;
        }
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string singleName, PhysicalFileIdentity expectedIdentity)
        {
            BeforeRemove?.Invoke(parent, singleName, expectedIdentity);
            _publication.RemoveControlFileAt(parent, singleName, expectedIdentity);
        }

        private sealed class ThrowingRelease(IAsyncDisposable inner) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                throw new IOException("injected native lock release failure");
            }
        }
    }
}
