using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nuplane.Loading;

namespace Nuplane.Loading.Api;

/// <summary>
/// Provides extension methods for mapping load-state-owned Nuplane endpoints.
/// </summary>
public static class NuplaneLoadStateEndpointExtensions
{
    /// <summary>
    /// Maps the optional load-state endpoint under the specified prefix.
    /// </summary>
    public static IEndpointRouteBuilder MapNuplaneLoadState(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/nuplane/admin")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{prefix}/load-state", async (
            [FromServices] IPackageLoadStateCatalog loadStateCatalog,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await loadStateCatalog.GetLoadStateAsync(cancellationToken);
            return Results.Ok(new PackageLoadStateResponse(snapshot));
        }).WithName("NuplaneGetLoadState")
          .WithTags("NuplaneLoadState");

        return endpoints;
    }
}

