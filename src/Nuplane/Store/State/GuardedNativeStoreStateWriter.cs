using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.State;

/// <summary>Writes one already-resolved state slot under its supplemental native slot guard.</summary>
/// <remarks>
/// This is an internal prerequisite for later serializer and registry routing. It does not acquire root/member
/// ownership and is not used by <see cref="StoreStateSerializer.SaveAsync"/> in this increment. The retained-path
/// callback replays retained ancestry and parent evidence, without pinning the replaceable final state-file identity.
/// It must not acquire root/member locks; this helper owns final state-slot identity transitions.
/// </remarks>
internal sealed class GuardedNativeStoreStateWriter
{
    private const long MaximumStatePayloadBytes = long.MaxValue;

    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStorePackageStreamFileSystem _streams;
    private readonly IPhysicalStorePublicationFileSystem _publication;
    private readonly IPackageProtectionBundleStatePayloadSerializer _payloadSerializer;
    private readonly PhysicalStoreStateSlotWriteGuard _guard;

    internal GuardedNativeStoreStateWriter(
        IPhysicalStoreFileSystem files,
        IPackageProtectionBundleStatePayloadSerializer payloadSerializer)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(payloadSerializer);
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem ?? throw Unsupported("Native state-slot name validation is unavailable.");
        _streams = files as IPhysicalStorePackageStreamFileSystem ?? throw Unsupported("Held-parent state streams are unavailable.");
        _publication = files as IPhysicalStorePublicationFileSystem ?? throw Unsupported("Held-parent state publication is unavailable.");
        _payloadSerializer = payloadSerializer;
        _guard = new PhysicalStoreStateSlotWriteGuard(files);
    }

    /// <summary>Publishes the supplied v1 state at its resolved parent/name after proving marker absence.</summary>
    /// <param name="parent">The already-resolved, retained parent directory handle.</param>
    /// <param name="slot">The exact current slot identity derived by the caller's resolver.</param>
    /// <param name="state">The v1 state payload to publish.</param>
    /// <param name="revalidateRetainedPath">A callback that replays retained ancestry and parent evidence without pinning the replaceable state-file identity or acquiring root/member locks.</param>
    /// <param name="cancellationToken">Cancellation requested before the native publication transition.</param>
    /// <returns>A verified committed state identity and whether exact recovery evidence remains.</returns>
    /// <exception cref="PackageStoreAdmissionException">The slot, marker, provider, path evidence, or publication outcome is unsafe or unknown.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurs before native publication.</exception>
    internal async Task<GuardedNativeStoreStateWriteOutcome> WriteAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        StoreStateRecord state,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(revalidateRetainedPath);
        cancellationToken.ThrowIfCancellationRequested();
        RefuseBundle(state, "A standalone state-slot writer cannot publish a v2 protection bundle.");

        await ReplayAndRequireUnboundAsync(parent, slot, null, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        var lease = await _guard.AcquireAsync(parent, slot, cancellationToken).ConfigureAwait(false);
        var prior = (PhysicalStoreEntryInfo?)null;
        var priorHash = (byte[]?)null;
        var temporary = (OwnedArtifact?)null;
        var backup = (OwnedArtifact?)null;
        var transitionAttempted = false;
        var preserveRecoveryEvidence = false;
        GuardedNativeStoreStateWriteOutcome? committedOutcome = null;
        Exception? operationError = null;
        try
        {
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            prior = ObserveStateSlot(parent, slot);
            if (prior is not null)
            {
                var existing = await ReadStateAsync(
                    parent, slot, prior, lease, revalidateRetainedPath, cancellationToken, computeCanonicalHash: false).ConfigureAwait(false);
                RefuseBundle(existing.State, "A standalone state-slot writer cannot replace a v2 protection bundle.");
                priorHash = existing.RawHash;
                backup = await StageBackupAsync(
                    parent, slot, prior, priorHash, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            }

            temporary = await StageStateAsync(
                parent, slot, state, prior, priorHash, lease, revalidateRetainedPath,
                () => preserveRecoveryEvidence = true, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            var current = ObserveStateSlot(parent, slot);
            if (current?.Identity != prior?.Identity)
                throw Unknown("The state-slot destination changed before the native publication transition.");
            if (prior is not null && priorHash is not null)
            {
                var currentHash = await HashStateSlotAsync(
                    parent, slot, prior, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(currentHash, priorHash))
                    throw Unknown("The prior state bytes changed after state staging and before native publication.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            RequireNativeEvidence(parent, slot, lease);
            current = ObserveStateSlot(parent, slot);
            if (current?.Identity != prior?.Identity)
                throw Unknown("The state-slot destination changed during final prepublication verification.");
            VerifyArtifact(parent, slot, temporary);
            if (backup is not null)
                VerifyArtifact(parent, slot, backup);
            cancellationToken.ThrowIfCancellationRequested();
            transitionAttempted = true;
            Exception? publicationError = null;
            try
            {
                _publication.PublishControlFileAt(
                    parent,
                    temporary.Name,
                    temporary.Identity,
                    slot.CanonicalBasename,
                    prior?.Identity);
            }
            catch (Exception exception)
            {
                publicationError = exception;
            }

            committedOutcome = await ClassifyTransitionAsync(
                parent, slot, lease, revalidateRetainedPath, temporary, backup, prior, priorHash,
                publicationError).ConfigureAwait(false);
        }
        catch (Exception caughtOperationError) when (!transitionAttempted)
        {
            if (preserveRecoveryEvidence)
            {
                operationError = caughtOperationError;
            }
            else if (temporary is null && backup is null)
            {
                try
                {
                    await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                    operationError = caughtOperationError;
                }
                catch (Exception replayError)
                {
                    operationError = Unknown(
                        "The state write failed without staged recovery artifacts and final retained-path replay failed.",
                        new AggregateException(caughtOperationError, replayError));
                }
            }
            else
            {
                try
                {
                    await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                    var current = ObserveStateSlot(parent, slot);
                    if (current?.Identity != prior?.Identity)
                        throw Unknown("The state slot changed before exact failure cleanup; recovery evidence is preserved.");
                    if (prior is not null && priorHash is not null)
                    {
                        var currentHash = await HashStateSlotAsync(
                            parent, slot, prior, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(currentHash, priorHash))
                            throw Unknown("The prior state bytes changed before exact failure cleanup; recovery evidence is preserved.");
                    }

                    if (temporary is not null)
                        RemoveArtifact(parent, slot, temporary);
                    if (backup is not null)
                        RemoveArtifact(parent, slot, backup);
                    await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                    if (ObserveStateSlot(parent, slot)?.Identity != prior?.Identity)
                        throw Unknown("The state slot changed during exact failure cleanup; recovery evidence was retained.");
                }
                catch (Exception cleanupSafetyError)
                {
                    operationError = Unknown(
                        "The state write failed before publication and exact cleanup could not be proven; recovery evidence was retained.",
                        new AggregateException(caughtOperationError, cleanupSafetyError));
                }

                operationError ??= caughtOperationError;
            }
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        Exception? guardReleaseError = null;
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { guardReleaseError = exception; }

        if (committedOutcome is not null)
            return committedOutcome with { GuardReleaseError = guardReleaseError };
        if (operationError is not null && guardReleaseError is not null)
            throw new AggregateException("The state write failed and its native slot guard could not be released cleanly.", operationError, guardReleaseError);
        if (operationError is not null)
            ExceptionDispatchInfo.Capture(operationError).Throw();
        if (guardReleaseError is not null)
            ExceptionDispatchInfo.Capture(guardReleaseError).Throw();
        throw new InvalidOperationException("The state writer finished without a verified outcome.");
    }

    private async Task<GuardedNativeStoreStateWriteOutcome> ClassifyTransitionAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        OwnedArtifact temporary,
        OwnedArtifact? backup,
        PhysicalStoreEntryInfo? prior,
        byte[]? priorHash,
        Exception? publicationError)
    {
        try
        {
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
            var destination = ObserveStateSlot(parent, slot);
            var stagedName = _files.InspectChildNoFollow(parent, temporary.Name);
            if (destination is { } published && published.Identity == temporary.Identity && stagedName is null)
            {
                var evidence = await ReadStateAsync(parent, slot, published, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                var expectedPayloadHash = temporary.PayloadHash;
                if (expectedPayloadHash is null || evidence.CanonicalHash is null || evidence.State.ProtectionBundle is not null ||
                    !CryptographicOperations.FixedTimeEquals(evidence.RawHash, expectedPayloadHash) ||
                    !CryptographicOperations.FixedTimeEquals(evidence.CanonicalHash, expectedPayloadHash))
                {
                    throw Unknown("The published state identity does not reopen as the exact staged payload.");
                }

                await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                var final = ObserveStateSlot(parent, slot);
                if (final?.Identity != temporary.Identity)
                    throw Unknown("The published state slot changed during final verification.");

                var recoveryRemains = await CleanupCommittedBackupAsync(
                    parent, slot, lease, revalidateRetainedPath, temporary, backup, expectedPayloadHash, priorHash).ConfigureAwait(false);
                return new GuardedNativeStoreStateWriteOutcome(temporary.Identity, recoveryRemains);
            }

            if (destination?.Identity == prior?.Identity && stagedName?.Identity == temporary.Identity)
            {
                if (prior is not null && priorHash is not null)
                {
                    var currentHash = await HashStateSlotAsync(parent, slot, prior, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(currentHash, priorHash))
                        throw Unknown("The prior state identity remains but its bytes changed during publication.");
                }

                await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                RemoveArtifact(parent, slot, temporary);
                if (backup is not null)
                    RemoveArtifact(parent, slot, backup);
                await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                if (ObserveStateSlot(parent, slot)?.Identity != prior?.Identity)
                    throw Unknown("The state slot changed during exact pre-transition cleanup.");
                if (publicationError is not null)
                    ExceptionDispatchInfo.Capture(publicationError).Throw();
                throw Unknown("The native publisher returned without publishing the staged state.");
            }

            throw Unknown("The native state publication outcome is ambiguous; staged and prior evidence were retained.", publicationError);
        }
        catch (Exception verificationError) when (!ReferenceEquals(verificationError, publicationError))
        {
            throw Unknown("The native publisher may have transitioned the state slot, but exact replay could not prove the outcome; recovery evidence was retained.",
                publicationError is null ? verificationError : new AggregateException(publicationError, verificationError));
        }
    }

    private async Task<OwnedArtifact> StageBackupAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo prior,
        byte[] priorHash,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        OwnedArtifact? artifact = null;
        ArtifactReservation? reservation = null;
        try
        {
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            artifact = CreateArtifact(parent, slot, ".bak", created => reservation = created);
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            await using var source = OpenStateRead(parent, slot, prior);
            source.Stream.Position = 0;
            var sourceHash = await HashStreamAsync(source.Stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, priorHash))
                throw Unknown("The prior state bytes changed before the backup copy.");
            source.Stream.Position = 0;
            var destination = artifact.File ?? throw new InvalidOperationException("The state backup handle is missing.");
            try
            {
                var parentInfo = RequireParent(parent, slot);
                await using (var output = _streams.CreatePackageFileWriteStream(
                                 parent, artifact.Name, destination, parentInfo, MaximumStatePayloadBytes))
                {
                    await source.Stream.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (output.Length != prior.Length)
                        throw Unknown("The byte-faithful state backup differs from the observed prior payload.");
                }
            }
            finally
            {
                destination.Dispose();
                artifact = artifact with { File = null };
                if (reservation is not null)
                    reservation.File = null;
            }

            source.Stream.Position = 0;
            var sourceHashAfterCopy = await HashStreamAsync(source.Stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sourceHashAfterCopy, priorHash))
                throw Unknown("The prior state bytes changed while its backup was staged.");
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            var backupEvidence = await HashArtifactAsync(parent, slot, artifact!, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            if (backupEvidence.Length != prior.Length || !CryptographicOperations.FixedTimeEquals(backupEvidence.Hash, priorHash))
                throw Unknown("The reopened state backup does not preserve the original bytes.");
            return artifact!;
        }
        catch (Exception operationError)
        {
            try
            {
                await CleanupFailedStagingAsync(
                    parent, slot, reservation, artifact, prior.Identity, priorHash,
                    lease, revalidateRetainedPath).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw Unknown(
                    "The state backup failed and exact cleanup or final replay could not be proven; evidence was retained.",
                    new AggregateException(operationError, cleanupError));
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw new InvalidOperationException("Unreachable state-backup failure path.");
        }
    }

    private async Task<OwnedArtifact> StageStateAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        StoreStateRecord state,
        PhysicalStoreEntryInfo? expectedPrior,
        byte[]? expectedPriorHash,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        Action recoveryEvidenceRetained,
        CancellationToken cancellationToken)
    {
        OwnedArtifact? artifact = null;
        ArtifactReservation? reservation = null;
        try
        {
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            if (ObserveStateSlot(parent, slot)?.Identity != expectedPrior?.Identity)
                throw Unknown("The state slot changed before state staging began.");
            artifact = CreateArtifact(parent, slot, ".tmp", created => reservation = created);
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            var parentInfo = RequireParent(parent, slot);
            var handle = artifact.File ?? throw new InvalidOperationException("The state stage handle is missing.");
            try
            {
                VerifyArtifactName(parent, slot, artifact.Name, _files.InspectHandle(handle).Identity);
                await using var output = _streams.CreatePackageFileWriteStream(
                    parent, artifact.Name, handle, parentInfo, MaximumStatePayloadBytes);
                artifact = artifact with { Identity = _files.InspectHandle(handle).Identity };
                await RunCodecAsync(
                    () => _payloadSerializer.WritePayloadAsync(output, state, cancellationToken),
                    parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                handle.Dispose();
                artifact = artifact with { File = null };
                if (reservation is not null)
                    reservation.File = null;
            }

            var stageInfo = VerifyArtifact(parent, slot, artifact!);
            var evidence = await ReadStateAsync(parent, slot, stageInfo, lease, revalidateRetainedPath, cancellationToken, artifact!.Name).ConfigureAwait(false);
            RefuseBundle(evidence.State, "A standalone state-slot writer cannot stage a v2 protection bundle.");
            if (evidence.CanonicalHash is null || !CryptographicOperations.FixedTimeEquals(evidence.RawHash, evidence.CanonicalHash))
                throw Unknown("The staged state payload does not round-trip through the selected serializer.");
            return artifact! with { PayloadHash = evidence.RawHash };
        }
        catch (Exception operationError)
        {
            try
            {
                await CleanupFailedStagingAsync(
                    parent, slot, reservation, artifact, expectedPrior?.Identity, expectedPriorHash,
                    lease, revalidateRetainedPath).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                recoveryEvidenceRetained();
                throw Unknown(
                    "The state stage failed and exact cleanup or final replay could not be proven; evidence was retained.",
                    new AggregateException(operationError, cleanupError));
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw new InvalidOperationException("Unreachable state-stage failure path.");
        }
    }

    private async Task CleanupFailedStagingAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        ArtifactReservation? reservation,
        OwnedArtifact? artifact,
        PhysicalFileIdentity? expectedStateIdentity,
        byte[]? expectedStateHash,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath)
    {
        if (artifact is not null)
            TryDisposeArtifactHandle(artifact);
        else if (reservation is not null)
            reservation.DisposeHandle();

        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        if (ObserveStateSlot(parent, slot)?.Identity != expectedStateIdentity)
            throw Unknown("The state slot changed while staging recovery evidence; evidence was retained.");
        if (expectedStateHash is not null)
        {
            var current = ObserveStateSlot(parent, slot)
                ?? throw Unknown("The prior state disappeared while staging recovery evidence; evidence was retained.");
            var currentHash = await HashStateSlotAsync(
                parent, slot, current, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedStateHash))
                throw Unknown("The prior state bytes changed while staging recovery evidence; evidence was retained.");
        }

        var ownedArtifact = artifact ?? reservation?.ToOwnedArtifact();
        if (reservation is not null && ownedArtifact is null)
            throw Unknown($"The created recovery entry '{reservation.Name}' has no verified handle identity; named evidence was retained.");
        if (ownedArtifact is not null)
            RemoveArtifact(parent, slot, ownedArtifact);

        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        if (ObserveStateSlot(parent, slot)?.Identity != expectedStateIdentity)
            throw Unknown("The state slot changed during recovery-evidence cleanup; evidence was retained.");
        if (ownedArtifact is not null && _files.InspectChildNoFollow(parent, ownedArtifact.Name) is not null)
            throw Unknown("An exactly identified recovery entry remains after failed-stage cleanup.");
    }

    private async Task<StatePayloadEvidence> ReadStateAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo fileInfo,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken,
        string? fileNameOverride = null,
        bool computeCanonicalHash = true)
    {
        var name = fileNameOverride ?? slot.CanonicalBasename;
        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        var expected = fileNameOverride is null ? ObserveStateSlot(parent, slot) : VerifyArtifact(parent, slot, new OwnedArtifact(name, fileInfo.Identity));
        if (expected?.Identity != fileInfo.Identity)
            throw Unknown("The state file changed before its held-handle payload read.");

        await using var openedFile = OpenStateRead(parent, slot, fileInfo, name);
        var payload = openedFile.Stream;
        var state = await RunCodecAsync(
            () => _payloadSerializer.ReadPayloadAsync(payload, cancellationToken),
            parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        var canonicalHash = computeCanonicalHash
            ? await HashPayloadAsync(state, parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!computeCanonicalHash)
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        payload.Position = 0;
        var rawHash = await HashStreamAsync(payload, cancellationToken).ConfigureAwait(false);
        RequireNativeEvidence(parent, slot, lease);
        var finalIdentity = fileNameOverride is null
            ? ObserveStateSlot(parent, slot)?.Identity
            : VerifyArtifact(parent, slot, new OwnedArtifact(name, fileInfo.Identity)).Identity;
        if (finalIdentity != fileInfo.Identity)
            throw Unknown("The state file identity changed during its final raw-byte verification.");
        return new StatePayloadEvidence(state, rawHash, canonicalHash);
    }

    private StateReadScope OpenStateRead(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo fileInfo,
        string? fileNameOverride = null)
    {
        var name = fileNameOverride ?? slot.CanonicalBasename;
        var file = _files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        try
        {
            var opened = _files.InspectHandle(file);
            RequireSingleLinkFile(opened, "The state payload is not one regular file.");
            if (opened.Identity != fileInfo.Identity)
                throw Unknown("The state payload changed between native observation and open.");
            VerifyArtifactName(parent, slot, name, opened.Identity);
            var parentInfo = RequireParent(parent, slot);
            var payload = _streams.OpenPackageArchiveReadStream(
                parent, name, file, parentInfo, opened, MaximumStatePayloadBytes);
            return new StateReadScope(file, payload);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private async Task<byte[]> HashPayloadAsync(
        StoreStateRecord state,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        await using var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        await RunCodecAsync(
            () => _payloadSerializer.WritePayloadAsync(sink, state, cancellationToken),
            parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        sink.FlushFinalBlock();
        return hash.Hash?.ToArray() ?? throw new CryptographicException("The canonical state payload hash was not finalized.");
    }

    private async Task<byte[]> HashStateSlotAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo expected,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        var evidence = await ReadStateAsync(
            parent, slot, expected, lease, revalidateRetainedPath, cancellationToken, computeCanonicalHash: false).ConfigureAwait(false);
        return evidence.RawHash;
    }

    private async Task<(byte[] Hash, long Length)> HashArtifactAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        OwnedArtifact artifact,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        var info = VerifyArtifact(parent, slot, artifact);
        var file = _files.OpenFileChildNoFollow(parent, artifact.Name, FileAccess.Read);
        try
        {
            var opened = _files.InspectHandle(file);
            RequireSingleLinkFile(opened, "A state recovery artifact is not one regular file.");
            if (opened.Identity != artifact.Identity || opened.Length != info.Length)
                throw Unknown("A state recovery artifact changed before reopen.");
            var parentInfo = RequireParent(parent, slot);
            await using var stream = _streams.OpenPackageArchiveReadStream(
                parent, artifact.Name, file, parentInfo, opened, MaximumStatePayloadBytes);
            var hash = await HashStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
            return (hash, opened.Length);
        }
        finally
        {
            file.Dispose();
        }
    }

    private static ValueTask<byte[]> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
        => SHA256.HashDataAsync(stream, cancellationToken);

    private async Task RunCodecAsync(
        Func<Task> codecOperation,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        await RunCodecAsync(async () =>
        {
            await codecOperation().ConfigureAwait(false);
            return true;
        }, parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunCodecAsync<T>(
        Func<Task<T>> codecOperation,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, cancellationToken).ConfigureAwait(false);
        T result = default!;
        Exception? codecError = null;
        try
        {
            result = await codecOperation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            codecError = exception;
        }

        try
        {
            await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception replayError)
        {
            if (codecError is not null)
                throw new AggregateException("Payload codec failed and retained state authority could not be revalidated.", codecError, replayError);
            throw;
        }

        if (codecError is not null)
            ExceptionDispatchInfo.Capture(codecError).Throw();
        return result;
    }

    private async Task ReplayAndRequireUnboundAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease? lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        CancellationToken cancellationToken)
    {
        RequireNativeEvidence(parent, slot, lease);
        Exception? callbackError = null;
        try
        {
            await revalidateRetainedPath(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            callbackError = exception;
        }

        Exception? invariantError = null;
        try
        {
            RequireNativeEvidence(parent, slot, lease);
        }
        catch (Exception exception)
        {
            invariantError = exception;
        }

        if (callbackError is not null && invariantError is not null)
            throw new AggregateException("Retained path replay failed and the native state slot was unsafe.", callbackError, invariantError);
        if (callbackError is not null)
            ExceptionDispatchInfo.Capture(callbackError).Throw();
        if (invariantError is not null)
            ExceptionDispatchInfo.Capture(invariantError).Throw();
    }

    private void RequireNativeEvidence(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease? lease)
    {
        lease?.RequireGroupBindingMarkerAbsent();
        ObserveStateSlot(parent, slot);
        lease?.Revalidate();
    }

    private PhysicalStoreEntryInfo? ObserveStateSlot(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot)
    {
        var parentInfo = RequireParent(parent, slot);
        var current = _files.InspectChildNoFollow(parent, slot.CanonicalBasename);
        if (current is null)
            return null;
        RequireSingleLinkFile(current, "The state slot is a link, non-regular entry, or multiply linked file.");
        var canonical = _names.ObserveCanonicalFileNameNoFollow(parent, slot.CanonicalBasename, current.Identity);
        if (canonical.ParentIdentity != parentInfo.Identity || canonical.Semantics != slot.NameSemantics ||
            !string.Equals(canonical.Basename, slot.CanonicalBasename, StringComparison.Ordinal))
        {
            throw Unknown("The state slot no longer has its exact retained basename and native lookup profile.");
        }

        var namedAgain = _files.InspectChildNoFollow(parent, slot.CanonicalBasename);
        RequireSingleLinkFile(namedAgain, "The state slot changed during native name validation.");
        if (namedAgain!.Identity != current.Identity)
            throw Unknown("The state slot identity changed during native name validation.");
        return namedAgain;
    }

    private PhysicalStoreEntryInfo RequireParent(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot)
    {
        var parentInfo = _files.InspectHandle(parent);
        if (parentInfo.Kind != PhysicalStoreEntryKind.Directory || parentInfo.Identity != slot.ParentIdentity)
            throw Unknown("The retained state parent differs from the resolved slot identity.");
        if (_names.ObserveDirectoryNameSemantics(parent) != slot.NameSemantics)
            throw Unknown("The retained state parent no longer has the resolved native name profile.");
        return parentInfo;
    }

    private OwnedArtifact CreateArtifact(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        string extension,
        Action<ArtifactReservation> registerOwnership)
    {
        var digest = PhysicalStoreStateSlotWriteGuard.GetSlotDigest(slot);
        var name = $".nuplane-state-{digest[..16]}-{Guid.NewGuid():N}{extension}";
        PhysicalStoreNames.ValidateSingleComponent(name);
        var reservation = new ArtifactReservation(name);
        registerOwnership(reservation);
        var handle = _files.CreateFileExclusiveAt(parent, name);
        reservation.File = handle;
        var info = _files.InspectHandle(handle);
        reservation.Identity = info.Identity;
        if (info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1 || info.Length != 0)
            throw Unknown("A newly created state recovery artifact is not an empty single-link regular file.");
        VerifyArtifactName(parent, slot, name, info.Identity);
        return new OwnedArtifact(name, info.Identity, handle);
    }

    private PhysicalStoreEntryInfo VerifyArtifact(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot, OwnedArtifact artifact)
    {
        var byName = _files.InspectChildNoFollow(parent, artifact.Name);
        RequireSingleLinkFile(byName, "A state recovery artifact is absent or unsafe.");
        if (byName!.Identity != artifact.Identity)
            throw Unknown("A state recovery artifact no longer has its exact created identity.");
        VerifyArtifactName(parent, slot, artifact.Name, artifact.Identity);
        return byName;
    }

    private void VerifyArtifactName(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        string name,
        PhysicalFileIdentity identity)
    {
        var canonical = _names.ObserveCanonicalFileNameNoFollow(parent, name, identity);
        if (canonical.ParentIdentity != slot.ParentIdentity || canonical.Semantics != slot.NameSemantics ||
            !string.Equals(canonical.Basename, name, StringComparison.Ordinal))
        {
            throw Unknown("A state recovery artifact is not the exact canonical entry under the retained parent.");
        }
    }

    private void RemoveArtifact(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot, OwnedArtifact artifact)
    {
        var current = _files.InspectChildNoFollow(parent, artifact.Name);
        if (current is null)
            return;
        RequireSingleLinkFile(current, "A state recovery artifact changed kind before exact cleanup.");
        if (current!.Identity != artifact.Identity)
            throw Unknown("A state recovery artifact changed identity before exact cleanup.");
        VerifyArtifactName(parent, slot, artifact.Name, artifact.Identity);
        _publication.RemoveControlFileAt(parent, artifact.Name, artifact.Identity);
        if (_files.InspectChildNoFollow(parent, artifact.Name) is not null)
            throw Unknown("An exactly identified state recovery artifact remains after cleanup.");
    }

    private async Task<bool> CleanupCommittedBackupAsync(
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease lease,
        Func<CancellationToken, ValueTask> revalidateRetainedPath,
        OwnedArtifact temporary,
        OwnedArtifact? backup,
        byte[] expectedPayloadHash,
        byte[]? expectedPriorHash)
    {
        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        var beforeCleanup = ObserveStateSlot(parent, slot);
        if (beforeCleanup?.Identity != temporary.Identity)
            throw Unknown("The committed state slot changed before recovery-artifact cleanup.");

        Exception? cleanupError = null;
        if (backup is not null)
        {
            try
            {
                RemoveArtifact(parent, slot, backup);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }
        }

        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        var committed = ObserveStateSlot(parent, slot);
        if (committed?.Identity != temporary.Identity)
            throw Unknown("The committed state slot changed during recovery-artifact cleanup.", cleanupError);
        var finalPayload = await ReadStateAsync(
            parent, slot, committed, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        if (finalPayload.CanonicalHash is null || finalPayload.State.ProtectionBundle is not null ||
            !CryptographicOperations.FixedTimeEquals(finalPayload.RawHash, expectedPayloadHash) ||
            !CryptographicOperations.FixedTimeEquals(finalPayload.CanonicalHash, expectedPayloadHash))
        {
            throw Unknown("The committed state payload changed during recovery-artifact cleanup.", cleanupError);
        }

        var recoveryRemains = false;
        if (backup is not null)
        {
            var currentBackup = _files.InspectChildNoFollow(parent, backup.Name);
            if (currentBackup is not null)
            {
                RequireSingleLinkFile(currentBackup, "The state backup changed kind during committed cleanup.");
                if (currentBackup.Identity != backup.Identity)
                    throw Unknown("The state backup changed identity during committed cleanup.", cleanupError);
                VerifyArtifactName(parent, slot, backup.Name, backup.Identity);
                var remaining = await HashArtifactAsync(
                    parent, slot, backup, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
                if (expectedPriorHash is null || !CryptographicOperations.FixedTimeEquals(remaining.Hash, expectedPriorHash))
                    throw Unknown("The retained state backup no longer preserves the original bytes.", cleanupError);
                recoveryRemains = true;
            }
        }

        await ReplayAndRequireUnboundAsync(parent, slot, lease, revalidateRetainedPath, CancellationToken.None).ConfigureAwait(false);
        if (ObserveStateSlot(parent, slot)?.Identity != temporary.Identity)
            throw Unknown("The committed state slot changed during final recovery verification.", cleanupError);
        if (cleanupError is not null && cleanupError is not IOException and not UnauthorizedAccessException)
            throw Unknown("Committed state is verified, but recovery-artifact cleanup failed with an unsafe provider outcome.", cleanupError);
        return cleanupError is not null && recoveryRemains;
    }

    private static void TryDisposeArtifactHandle(OwnedArtifact artifact)
    {
        try { artifact.File?.Dispose(); }
        catch { }
    }

    private static void RequireSingleLinkFile(PhysicalStoreEntryInfo? info, string message)
    {
        if (info is null || info.Kind != PhysicalStoreEntryKind.RegularFile || info.LinkCount != 1)
            throw Unknown(message);
    }

    private static void RefuseBundle(StoreStateRecord state, string message)
    {
        if (state.ProtectionBundle is not null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant, message);
    }

    private static PackageStoreAdmissionException Unsupported(string message)
        => new(PackageStoreAdmissionReason.UnsupportedFilesystem, message);

    private static PackageStoreAdmissionException Unknown(string message, Exception? innerException = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, innerException: innerException);

    private sealed record StatePayloadEvidence(StoreStateRecord State, byte[] RawHash, byte[]? CanonicalHash);

    private sealed record OwnedArtifact(string Name, PhysicalFileIdentity Identity, PhysicalStoreFileHandle? File = null, byte[]? PayloadHash = null);

    private sealed class ArtifactReservation(string name)
    {
        internal string Name { get; } = name;
        internal PhysicalStoreFileHandle? File { get; set; }
        internal PhysicalFileIdentity? Identity { get; set; }

        internal OwnedArtifact? ToOwnedArtifact()
            => Identity is { } identity ? new OwnedArtifact(Name, identity, File) : null;

        internal void DisposeHandle()
        {
            try { File?.Dispose(); }
            catch { }
            File = null;
        }
    }

    private sealed class StateReadScope(PhysicalStoreFileHandle file, Stream stream) : IAsyncDisposable
    {
        internal PhysicalStoreFileHandle File { get; } = file;
        internal Stream Stream { get; } = stream;
        public async ValueTask DisposeAsync()
        {
            try { await Stream.DisposeAsync().ConfigureAwait(false); }
            finally { File.Dispose(); }
        }
    }
}

/// <summary>Reports a state transition proven from reopened native identity and payload evidence.</summary>
internal sealed record GuardedNativeStoreStateWriteOutcome(
    PhysicalFileIdentity StateIdentity,
    bool RecoveryArtifactsRemain,
    Exception? GuardReleaseError = null);
