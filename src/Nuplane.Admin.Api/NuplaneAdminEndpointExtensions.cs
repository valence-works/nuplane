using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nuplane.Contracts;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Admin.Api;

/// <summary>
/// Provides extension methods for mapping Nuplane admin endpoints to an ASP.NET Core endpoint routing builder.
/// </summary>
public static class NuplaneAdminEndpointExtensions
{
    /// <summary>
    /// Maps Nuplane admin operational endpoints under the specified prefix.
    /// Provides:
    /// <list type="bullet">
    ///   <item><c>GET {prefix}/snapshot</c> — Returns a consistent operational snapshot.</item>
    ///   <item><c>POST {prefix}/reconcile</c> — Triggers a manual reconciliation cycle.</item>
    /// </list>
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The URL prefix for admin endpoints. Defaults to <c>/nuplane/admin</c>.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapNuplaneAdmin(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/nuplane/admin")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{prefix}/snapshot", async (
            INuplaneOperationalSurface surface,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await surface.GetSnapshotAsync(cancellationToken);
            return Results.Ok(new SnapshotResponse(snapshot));
        }).WithName("NuplaneGetSnapshot")
          .WithTags("NuplaneAdmin");

        endpoints.MapPost($"{prefix}/reconcile", async (
            INuplaneOperationalSurface surface,
            CancellationToken cancellationToken) =>
        {
            var outcome = await surface.TriggerReconcileAsync(cancellationToken);
            return outcome.OutcomeCode switch
            {
                ManualReconcileOutcomeCode.Completed => Results.Ok(new ReconcileResponse(outcome)),
                ManualReconcileOutcomeCode.Accepted => Results.Accepted(null, new ReconcileResponse(outcome)),
                ManualReconcileOutcomeCode.Rejected => Results.Conflict(new ReconcileResponse(outcome)),
                ManualReconcileOutcomeCode.Unavailable => Results.StatusCode(503),
                _ => Results.StatusCode(500)
            };
        }).WithName("NuplaneTriggerReconcile")
          .WithTags("NuplaneAdmin");

        return endpoints;
    }
}