using Nuplane.Abstractions;

namespace Nuplane.Admin.Api;

internal sealed record PackageCatalogResponse(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackageDescriptor> Packages,
    string CorrelationId)
{
    public PackageCatalogResponse(ActivePackageCatalogSnapshot snapshot)
        : this(snapshot.SnapshotAtUtc, snapshot.PersistedAtUtc, snapshot.Packages, snapshot.CorrelationId)
    {
    }
}

