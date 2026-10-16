using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Registration;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.State;

/// <summary>
/// Serializes and deserializes <see cref="StoreStateRecord"/> to/from JSON files.
/// </summary>
public sealed class StoreStateSerializer : IPackageProtectionBundleStatePayloadSerializer, IStoreStatePayloadObservationSerializer
{
    private readonly AtomicFileWriter _fileWriter;
    private readonly bool _refuseUnscopedPathAccess;
    internal bool IsParticipantGuarded => _refuseUnscopedPathAccess;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new PackageProtectionRecordJsonConverter(), new PackageProtectionBundleJsonConverter() }
    };

    /// <summary>Initializes a serializer that writes state through atomic file replacement.</summary>
    public StoreStateSerializer()
        : this(new AtomicFileWriter(), refuseUnscopedPathAccess: false)
    {
    }

    internal StoreStateSerializer(PackageStoreCompositionSnapshot composition)
        : this(new AtomicFileWriter(), (composition ?? throw new ArgumentNullException(nameof(composition))).IsParticipant)
    {
    }

    internal StoreStateSerializer(IAtomicFileReplacer fileReplacer)
        : this(new AtomicFileWriter(fileReplacer), refuseUnscopedPathAccess: false)
    {
    }

    private StoreStateSerializer(AtomicFileWriter fileWriter, bool refuseUnscopedPathAccess)
    {
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _refuseUnscopedPathAccess = refuseUnscopedPathAccess;
    }

    /// <inheritdoc />
    public async Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
    {
        RefuseUnscopedPathAccess();
        if (!File.Exists(stateFilePath))
        {
            return StoreStateRecord.Empty();
        }

        await using var stream = new FileStream(
            stateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes and normalizes a <see cref="StoreStateRecord"/> from an already-open
    /// <paramref name="stream"/>. Shared by <see cref="LoadAsync"/> and by callers, such as
    /// <see cref="NuplaneStore"/>, that need to open the state file with sharing semantics of
    /// their own instead of going through <see cref="LoadAsync"/>.
    /// </summary>
    internal static async Task<StoreStateRecord> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var observation = await DeserializeForObservationAsync(stream, cancellationToken).ConfigureAwait(false);
        return observation.State ?? StoreStateRecord.Empty();
    }

    async Task<StoreStatePayloadObservation> IStoreStatePayloadObservationSerializer.ReadPayloadForObservationAsync(
        Stream payload,
        CancellationToken cancellationToken)
        => await DeserializeForObservationAsync(payload, cancellationToken).ConfigureAwait(false);

    private static async Task<StoreStatePayloadObservation> DeserializeForObservationAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var stateFile = await JsonSerializer.DeserializeAsync<StoreStateFileDto>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return stateFile is null
            ? new StoreStatePayloadObservation(IsNullPayload: true, State: null)
            : new StoreStatePayloadObservation(IsNullPayload: false,
                State: Normalize(stateFile.ToStoreStateRecord()));
    }

    /// <inheritdoc />
    public Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return DeserializeAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
    {
        RefuseUnscopedPathAccess();
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        RefuseLegacyPathWriteForBundle(state, "A path-based state write cannot publish a v2 protection bundle.");
        if (File.Exists(stateFilePath))
        {
            var current = await LoadAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
            RefuseLegacyPathWriteForBundle(current, "A path-based state write cannot replace an existing v2 protection bundle.");
        }

        await _fileWriter.WriteAsync(
            stateFilePath,
            (stream, token) => WritePayloadAsync(stream, state, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(state);
        var stateDto = StoreStateFileDto.FromState(Normalize(state));
        await JsonSerializer.SerializeAsync(
            payload,
            stateDto,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    internal static StoreStateRecord Normalize(StoreStateRecord state) =>
        state with
        {
            ActivePackageDescriptorsById = new(state.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(state.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase),
            ProtectionRecord = state.ProtectionRecord,
            ProtectionBundle = state.ProtectionBundle?.Copy()
        };

    private static void RefuseLegacyPathWriteForBundle(StoreStateRecord state, string message)
    {
        if (state.ProtectionBundle is not null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant, message);
    }

    private void RefuseUnscopedPathAccess()
    {
        if (_refuseUnscopedPathAccess)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Path-based store-state access is unavailable in coordinated participant composition; use a live operation borrow.");
    }
}
