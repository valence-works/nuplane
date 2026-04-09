using Nuplane.Admin;
using Nuplane.Loading;

namespace Nuplane.Admin.Api;

internal sealed record LoadingCatalogResponse(
    bool IsAvailable,
    LoadingCatalogSnapshot? Snapshot,
    string? Reason,
    string CorrelationId)
{
    public LoadingCatalogResponse(AdminLoadingCatalogReadResult result)
        : this(result.IsAvailable, result.Snapshot, result.Reason, result.CorrelationId)
    {
    }
}

