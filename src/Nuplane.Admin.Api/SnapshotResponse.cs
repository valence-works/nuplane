using Nuplane.Runtime.Operational;

namespace Nuplane.Admin.Api;

/// <summary>
/// Response DTO for operational snapshot endpoint.
/// </summary>
internal sealed record SnapshotResponse(
    DateTimeOffset SnapshotAtUtc,
    IReadOnlyList<ActivePackageEntry> ActivePackages,
    LastReconcileOutcome? LastReconcile,
    string Health,
    IReadOnlyList<string> DegradedReasons,
    string CorrelationId)
{
    /// <summary>
    /// Initializes from an <see cref="OperationalSnapshot"/>.
    /// </summary>
    public SnapshotResponse(OperationalSnapshot snapshot)
        : this(
            snapshot.SnapshotAtUtc,
            snapshot.ActivePackages,
            snapshot.LastReconcile,
            snapshot.Health.ToString(),
            snapshot.DegradedReasons,
            snapshot.CorrelationId)
    {
    }
}