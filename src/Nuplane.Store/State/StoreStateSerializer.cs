using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;

namespace Nuplane.Store.State;

public sealed record FailureRecord(
    string PackageId,
    string Stage,
    string Message,
    DateTimeOffset OccurredAt,
    string CorrelationId);

public sealed record SourceSnapshotRef(
    string Version,
    DateTimeOffset CapturedAt,
    IReadOnlyList<PackageRequest>? Requests = null);

public sealed record StoreStateRecord(
    Dictionary<string, string> ActiveVersionById,
    Dictionary<string, string> LastKnownGoodById,
    Dictionary<string, FailureRecord> LastFailureById,
    Dictionary<string, SourceSnapshotRef> LastSuccessfulSourceSnapshots,
    DateTimeOffset UpdatedAt)
{
    public static StoreStateRecord Empty() =>
        new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);
}

public sealed class StoreStateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
