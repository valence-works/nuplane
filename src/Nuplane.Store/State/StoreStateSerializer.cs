using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;

namespace Nuplane.Store.State;

/// <summary>
/// Records a failure that occurred during package reconciliation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Stage">The reconciliation stage where the failure occurred.</param>
/// <param name="Message">A descriptive error message.</param>
/// <param name="OccurredAt">The time at which the failure occurred.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
public sealed record FailureRecord(
    string PackageId,
    string Stage,
    string Message,
    DateTimeOffset OccurredAt,
    string CorrelationId);

/// <summary>
/// References a desired-state source snapshot, capturing the version, timestamp, and cached requests.
/// </summary>
/// <param name="Version">The snapshot version identifier.</param>
/// <param name="CapturedAt">The time at which the snapshot was captured.</param>
/// <param name="Requests">The cached package requests from this snapshot, if available.</param>
public sealed record SourceSnapshotRef(
    string Version,
    DateTimeOffset CapturedAt,
    IReadOnlyList<PackageRequest>? Requests = null);

/// <summary>
/// Represents the complete persisted reconciliation state, including active versions,
/// last-known-good versions, failure records, and source snapshots.
/// </summary>
/// <param name="ActiveVersionById">Dictionary mapping package identifiers to their active versions.</param>
/// <param name="LastKnownGoodById">Dictionary mapping package identifiers to their last-known-good versions.</param>
/// <param name="LastFailureById">Dictionary mapping package identifiers to their most recent failure records.</param>
/// <param name="LastSuccessfulSourceSnapshots">Dictionary mapping source names to their snapshot references.</param>
/// <param name="UpdatedAt">The time at which the state was last updated.</param>
public sealed record StoreStateRecord(
    Dictionary<string, string> ActiveVersionById,
    Dictionary<string, string> LastKnownGoodById,
    Dictionary<string, FailureRecord> LastFailureById,
    Dictionary<string, SourceSnapshotRef> LastSuccessfulSourceSnapshots,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Creates an empty store state record with the current timestamp.
    /// </summary>
    public static StoreStateRecord Empty() =>
        new(
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);
}

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
