using Nuplane.Loading;

namespace Nuplane.Loading.Api;

internal sealed record PackageLoadStateResponse(
    PackageLoadStateAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<PackageLoadState> Packages,
    string? Reason,
    string CorrelationId)
{
    public PackageLoadStateResponse(PackageLoadStateSnapshot snapshot)
        : this(
            snapshot.Availability,
            snapshot.SnapshotAtUtc,
            snapshot.RefreshedAtUtc,
            snapshot.Packages,
            snapshot.Reason,
            snapshot.CorrelationId)
    {
    }
}

