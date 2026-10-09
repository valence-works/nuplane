using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Store.State.ProtectionSerialization;

namespace Nuplane.Store.State;

/// <summary>
/// Serializes and deserializes <see cref="StoreStateRecord"/> to/from JSON files.
/// </summary>
public sealed class StoreStateSerializer : IPackageProtectionStateSerializer
{
    private readonly AtomicFileWriter _fileWriter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new PackageProtectionRecordJsonConverter() }
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
        return await DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes and normalizes a <see cref="StoreStateRecord"/> from an already-open
    /// <paramref name="stream"/>. Shared by <see cref="LoadAsync"/> and by callers, such as
    /// <see cref="NuplaneStore"/>, that need to open the state file with sharing semantics of
    /// their own instead of going through <see cref="LoadAsync"/>.
    /// </summary>
    internal static async Task<StoreStateRecord> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var stateFile = await JsonSerializer.DeserializeAsync<StoreStateFileDto>(stream, JsonOptions, cancellationToken);
        return Normalize(stateFile?.ToStoreStateRecord() ?? StoreStateRecord.Empty());
    }

    /// <inheritdoc />
    public async Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
    {
        await _fileWriter.WriteAsync(
            stateFilePath,
            async (stream, token) =>
            {
                await JsonSerializer.SerializeAsync(stream, StoreStateFileDto.FromState(Normalize(state)), JsonOptions, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    internal static StoreStateRecord Normalize(StoreStateRecord state) =>
        state with
        {
            ActivePackageDescriptorsById = new(state.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(state.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase)
        };
}
