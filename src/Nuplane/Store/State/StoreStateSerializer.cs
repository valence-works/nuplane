using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuplane.Store.State;

/// <summary>
/// Serializes and deserializes <see cref="StoreStateRecord"/> to/from JSON files.
/// </summary>
public sealed class StoreStateSerializer : IStoreStateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public async Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(stateFilePath))
        {
            return StoreStateRecord.Empty();
        }

        await using var stream = File.OpenRead(stateFilePath);
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
        var state = await JsonSerializer.DeserializeAsync<StoreStateRecord>(stream, JsonOptions, cancellationToken);
        return Normalize(state ?? StoreStateRecord.Empty());
    }

    /// <inheritdoc />
    public async Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(stateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(stateFilePath);
        await JsonSerializer.SerializeAsync(stream, Normalize(state), JsonOptions, cancellationToken);
    }

    private static StoreStateRecord Normalize(StoreStateRecord state) =>
        state with
        {
            ActivePackageDescriptorsById = new(state.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(state.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase)
        };
}
