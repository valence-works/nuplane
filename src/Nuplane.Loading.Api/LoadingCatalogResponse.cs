using Nuplane.Loading;

namespace Nuplane.Loading.Api;

internal sealed record LoadingCatalogResponse(
    LoadingCatalogAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<LoadingPackageDescriptor> Packages,
    string? Reason,
    string CorrelationId)
{
    public LoadingCatalogResponse(LoadingCatalogSnapshot snapshot)
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

