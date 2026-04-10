using Nuplane.Abstractions;
using Nuplane.Operational;

namespace Nuplane.Admin.Api;

/// <summary>
/// Response DTO for operational snapshot endpoint.
/// </summary>
internal sealed record SnapshotResponse(
    DateTimeOffset SnapshotAtUtc,
    IReadOnlyList<ActivePackageDescriptor> ActivePackages,
    LastReconcileOutcome? LastReconcile,
    string Health,
    IReadOnlyList<string> DegradedReasons,
    string CorrelationId)
{
    /// <summary>
    /// Initializes from separate active package and operational state snapshots.
    /// </summary>
    public SnapshotResponse(ActivePackageCatalogSnapshot packages, OperationalStateSnapshot state)
        : this(
            state.SnapshotAtUtc,
            packages.Packages,
            state.LastReconcile,
            state.Health.ToString(),
            state.DegradedReasons,
            state.CorrelationId)
    {
    }
}