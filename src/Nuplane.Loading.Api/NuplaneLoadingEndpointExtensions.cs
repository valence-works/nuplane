using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nuplane.Loading;

namespace Nuplane.Loading.Api;

/// <summary>
/// Provides extension methods for mapping loading-owned Nuplane endpoints.
/// </summary>
public static class NuplaneLoadingEndpointExtensions
{
    /// <summary>
    /// Maps the optional loading-catalog endpoint under the specified prefix.
    /// </summary>
    public static IEndpointRouteBuilder MapNuplaneLoading(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/nuplane/admin")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{prefix}/loading", async (
            [FromServices] ILoadingCatalog loadingCatalog,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await loadingCatalog.GetSnapshotAsync(cancellationToken);
            return Results.Ok(new LoadingCatalogResponse(snapshot));
        }).WithName("NuplaneGetLoading")
          .WithTags("NuplaneLoading");

        return endpoints;
    }
}

