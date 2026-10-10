using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.State;

/// <summary>
/// Serializes and deserializes <see cref="StoreStateRecord"/> to/from JSON files.
/// </summary>
public sealed class StoreStateSerializer : IPackageProtectionBundleStatePayloadSerializer
{
    private readonly AtomicFileWriter _fileWriter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new PackageProtectionRecordJsonConverter(), new PackageProtectionBundleJsonConverter() }
    };

    /// <summary>Initializes a serializer that writes state through atomic file replacement.</summary>
    public StoreStateSerializer()
        : this(new AtomicFileWriter())
    {
    }

    internal StoreStateSerializer(IAtomicFileReplacer fileReplacer)
        : this(new AtomicFileWriter(fileReplacer))
    {
    }

    private StoreStateSerializer(AtomicFileWriter fileWriter)
    {
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
    }

    /// <inheritdoc />
    public async Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
    {
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
        ArgumentNullException.ThrowIfNull(stream);
        var stateFile = await JsonSerializer.DeserializeAsync<StoreStateFileDto>(stream, JsonOptions, cancellationToken);
        return Normalize(stateFile?.ToStoreStateRecord() ?? StoreStateRecord.Empty());
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
}
