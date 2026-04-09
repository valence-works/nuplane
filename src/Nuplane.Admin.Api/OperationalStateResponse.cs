using Nuplane.Operational;

namespace Nuplane.Admin.Api;

internal sealed record OperationalStateResponse(
    DateTimeOffset SnapshotAtUtc,
    LastReconcileOutcome? LastReconcile,
    string Health,
    IReadOnlyList<string> DegradedReasons,
    string CorrelationId)
{
    public OperationalStateResponse(OperationalStateSnapshot snapshot)
        : this(
            snapshot.SnapshotAtUtc,
            snapshot.LastReconcile,
            snapshot.Health.ToString(),
            snapshot.DegradedReasons,
            snapshot.CorrelationId)
    {
    }
}

