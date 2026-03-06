using Nuplane.Abstractions;

namespace Nuplane.Store.State;

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