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
        var state = await JsonSerializer.DeserializeAsync<StoreStateRecord>(stream, JsonOptions, cancellationToken);
        return state ?? StoreStateRecord.Empty();
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
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }
}
