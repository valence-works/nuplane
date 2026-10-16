using System.Security.Cryptography;
using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Reads one bounded state payload through an already-retained native parent and slot.</summary>
internal static class NativeStoreStatePayloadReader
{
    internal static async Task<NativeStoreStatePayloadObservation> ReadAsync(
        IPhysicalStoreFileSystem files,
        IStoreStateSerializer serializer,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalFileIdentity? expectedIdentity,
        string? expectedBodyDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(slot);
        cancellationToken.ThrowIfCancellationRequested();

        if (serializer is not IPackageProtectionStatePayloadSerializer payloadSerializer ||
            files is not IPhysicalStoreStateFileSystem stateFiles)
            return NativeStoreStatePayloadObservation.Unavailable(slot, "The configured participant cannot read bounded state payloads through caller-owned native streams.");

        var before = files.InspectChildNoFollow(parent, slot.CanonicalBasename);
        if (before is null)
            return NativeStoreStatePayloadObservation.Missing(slot);
        if (before.Kind != PhysicalStoreEntryKind.RegularFile)
            return NativeStoreStatePayloadObservation.Unavailable(slot, "The state slot is not a regular no-follow file.", before.Identity);

        if (expectedIdentity is not null && before.Identity != expectedIdentity)
            return new NativeStoreStatePayloadObservation(NativeStoreStatePayloadStatus.Changed, slot,
                before.Identity, null, null, null,
                "The selected state slot no longer has the expected native file identity.");

        byte[] bytes;
        try
        {
            bytes = await ReadNativeBytesAsync(files, stateFiles, parent, slot, before,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          PackageStoreAdmissionException)
        {
            return NativeStoreStatePayloadObservation.Unavailable(slot,
                "The selected state payload could not be opened and read through its retained native slot.", before.Identity,
                exception.GetType().Name);
        }

        var rawDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        StoreStateRecord? state = null;
        var nullPayload = false;
        var strictObservation = serializer is IStoreStatePayloadObservationSerializer;
        Exception? decodeFailure = null;
        try
        {
            using var payload = new MemoryStream(bytes, writable: false);
            if (serializer is IStoreStatePayloadObservationSerializer observingSerializer)
            {
                var read = await observingSerializer.ReadPayloadForObservationAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                nullPayload = read.IsNullPayload;
                state = read.State;
            }
            else
            {
                state = await payloadSerializer.ReadPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
                nullPayload = state is null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            decodeFailure = exception;
        }

        // The serializer is caller supplied and may await or re-enter host code. Replay the exact native slot and
        // bounded bytes only after all decoding work completes; resolver ancestry/alias replay is performed by
        // the retained referent owner after this method returns.
        cancellationToken.ThrowIfCancellationRequested();
        var changed = !IsSameRegularFile(files, parent, slot, before.Identity, expectedIdentity);
        if (!changed)
        {
            try
            {
                var reread = await ReadNativeBytesAsync(files, stateFiles, parent, slot, before,
                    cancellationToken).ConfigureAwait(false);
                changed = !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(reread)) ||
                          !IsSameRegularFile(files, parent, slot, before.Identity, expectedIdentity);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              PackageStoreAdmissionException)
            {
                changed = true;
            }
        }

        if (changed)
            return new NativeStoreStatePayloadObservation(NativeStoreStatePayloadStatus.Changed, slot,
                before.Identity, rawDigest, state is null ? null : ProtectionDigest.StateBody(state), state,
                "The state slot identity or bounded payload bytes changed during asynchronous observation.");

        if (decodeFailure is not null)
            return NativeStoreStatePayloadObservation.Malformed(slot, before.Identity, rawDigest,
                decodeFailure is JsonException
                    ? "The observed state payload is malformed or unsupported."
                    : "The configured state serializer could not decode the observed payload.",
                decodeFailure.GetType().Name);

        if (nullPayload || state is null)
            return NativeStoreStatePayloadObservation.Null(slot, before.Identity, rawDigest, null);

        var bodyDigest = ProtectionDigest.StateBody(state);
        if (expectedBodyDigest is not null && !string.Equals(bodyDigest, expectedBodyDigest, StringComparison.Ordinal))
        {
            return new NativeStoreStatePayloadObservation(NativeStoreStatePayloadStatus.Changed, slot,
                before.Identity, rawDigest, bodyDigest, state,
                "The decoded state body does not match its retained membership digest.");
        }

        if (!strictObservation)
            return new NativeStoreStatePayloadObservation(NativeStoreStatePayloadStatus.Unavailable, slot,
                before.Identity, rawDigest, bodyDigest, state,
                "The configured custom serializer does not expose strict payload observation semantics; decoded fields are retained as unverified metadata.");

        return new NativeStoreStatePayloadObservation(
            NativeStoreStatePayloadStatus.Observed,
            slot,
            before.Identity,
            rawDigest,
            bodyDigest,
            state,
            null);
    }

    private static bool IsSameRegularFile(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalFileIdentity identity,
        PhysicalFileIdentity? expectedIdentity)
    {
        var observed = files.InspectChildNoFollow(parent, slot.CanonicalBasename);
        return observed is { Kind: PhysicalStoreEntryKind.RegularFile } && observed.Identity == identity &&
               (expectedIdentity is null || observed.Identity == expectedIdentity);
    }

    private static async Task<byte[]> ReadNativeBytesAsync(
        IPhysicalStoreFileSystem files,
        IPhysicalStoreStateFileSystem stateFiles,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        PhysicalStoreEntryInfo expectedEntry,
        CancellationToken cancellationToken)
    {
        using var stream = stateFiles.OpenStatePayloadReadStream(parent, slot, expectedEntry,
            RootMembershipRegistry.MaximumStateBytes);
        var bytes = await ReadBoundedAsync(stream, RootMembershipRegistry.MaximumStateBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!IsSameRegularFile(files, parent, slot, expectedEntry.Identity, expectedEntry.Identity))
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The selected state slot changed while its bounded payload bytes were read.");
        return bytes;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new IOException("The state payload exceeds the configured read bound.");
            buffer.Write(chunk, 0, read);
        }
    }
}

internal enum NativeStoreStatePayloadStatus
{
    Observed,
    Missing,
    NullPayload,
    Malformed,
    Changed,
    Unavailable
}

internal sealed class NativeStoreStatePayloadObservation(
    NativeStoreStatePayloadStatus status,
    StateSlotIdentity? slot,
    PhysicalFileIdentity? stateFileIdentity,
    string? rawPayloadDigest,
    string? stateBodyDigest,
    StoreStateRecord? state,
    string? diagnostic)
{
    internal NativeStoreStatePayloadStatus Status { get; } = status;
    internal StateSlotIdentity? Slot { get; } = slot;
    internal PhysicalFileIdentity? StateFileIdentity { get; } = stateFileIdentity;
    internal string? RawPayloadDigest { get; } = rawPayloadDigest;
    internal string? StateBodyDigest { get; } = stateBodyDigest;
    internal StoreStateRecord? State { get; } = state;
    internal string? Diagnostic { get; } = diagnostic;

    internal static NativeStoreStatePayloadObservation Missing(StateSlotIdentity slot)
        => new(NativeStoreStatePayloadStatus.Missing, slot, null, null, null, null,
            "The configured state slot is positively absent.");

    internal static NativeStoreStatePayloadObservation Null(StateSlotIdentity slot, PhysicalFileIdentity identity,
        string rawDigest, string? diagnostic)
        => new(NativeStoreStatePayloadStatus.NullPayload, slot, identity, rawDigest, null, null,
            diagnostic ?? "The serialized state payload is JSON null or the configured serializer returned no state.");

    internal static NativeStoreStatePayloadObservation Malformed(StateSlotIdentity slot, PhysicalFileIdentity identity,
        string rawDigest, string diagnostic, string? exception)
        => new(NativeStoreStatePayloadStatus.Malformed, slot, identity, rawDigest, null, null,
            exception is null ? diagnostic : $"{diagnostic} ({exception}).");

    internal static NativeStoreStatePayloadObservation Unavailable(StateSlotIdentity slot, string diagnostic,
        PhysicalFileIdentity? identity = null, string? exception = null)
        => new(NativeStoreStatePayloadStatus.Unavailable, slot, identity, null, null, null,
            exception is null ? diagnostic : $"{diagnostic} ({exception}).");
}
