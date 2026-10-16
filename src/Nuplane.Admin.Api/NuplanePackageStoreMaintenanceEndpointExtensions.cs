using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Nuplane.Admin.Api;

/// <summary>Maps explicitly enabled manual maintenance endpoints over configured root labels.</summary>
public static class NuplanePackageStoreMaintenanceEndpointExtensions
{
    private static readonly JsonSerializerOptions EnrollmentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Maps explicit enrollment, migration and recovery, enrollment preview, inspection and manual prune endpoints.</summary>
    /// <remarks>Requires AddNuplanePackageStoreMaintenance. Apply the host's authorization policy to the returned group.</remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The endpoint group prefix.</param>
    /// <returns>The mapped group, allowing the host to require its own authorization policy.</returns>
    public static RouteGroupBuilder MapNuplanePackageStoreMaintenance(
        this IEndpointRouteBuilder endpoints, string prefix = "/nuplane/admin/store")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix).WithTags("NuplanePackageStoreMaintenance");
        group.MapGet("/enrollment", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            string? rootLabel, CancellationToken cancellationToken) =>
                RunAsync(async () => Results.Ok(await operations.PreviewEnrollmentAsync(rootLabel ?? "default", cancellationToken))))
            .WithName("NuplanePreviewPackageStoreEnrollment");
        group.MapPost("/enrollment", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            HttpRequest request, CancellationToken cancellationToken) =>
                RunAsync(async () => Report(await operations.EnrollAsync(
                    await ReadEnrollmentAsync(request, cancellationToken), cancellationToken))))
            .WithName("NuplaneEnrollPackageStore");
        group.MapPost("/enrollment/recover", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            HttpRequest request, CancellationToken cancellationToken) =>
                RunAsync(async () => Report(await operations.RecoverEnrollmentAsync(
                    await ReadEnrollmentAsync(request, cancellationToken), cancellationToken))))
            .WithName("NuplaneRecoverPackageStoreEnrollment");
        group.MapPost("/enrollment/migrate", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            HttpRequest request, CancellationToken cancellationToken) =>
                RunAsync(async () => Report(await operations.MigrateAsync(
                    await ReadEnrollmentAsync(request, cancellationToken), cancellationToken))))
            .WithName("NuplaneMigratePackageStoreEnrollment");
        group.MapGet("/inspection", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            string? rootLabel, CancellationToken cancellationToken) =>
                RunAsync(async () => Report(await operations.InspectAsync(rootLabel ?? "default", cancellationToken))))
            .WithName("NuplaneInspectPackageStore");
        group.MapPost("/prune", (
            [FromServices] INuplanePackageStoreMaintenanceOperations operations,
            [FromBody] PackageStorePruneRequest request, CancellationToken cancellationToken) =>
                RunAsync(async () => Report(await operations.PruneAsync(request, cancellationToken))))
            .WithName("NuplanePrunePackageStore");
        return group;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new PackageStoreMaintenanceDiagnostic("InvalidRequest", exception.Message));
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new PackageStoreMaintenanceDiagnostic("InvalidRequest", exception.Message));
        }
    }

    private static async Task<PackageStoreEnrollmentRequest> ReadEnrollmentAsync(HttpRequest request, CancellationToken token)
    {
        if (!request.HasJsonContentType())
            throw new ArgumentException("Enrollment requires a JSON request body.");
        return await request.ReadFromJsonAsync<PackageStoreEnrollmentRequest>(EnrollmentJsonOptions, token)
            ?? throw new ArgumentException("Enrollment requires a non-null request body.");
    }

    private static IResult Report(PackageStoreEnrollmentResult result)
        => Results.Json(result, statusCode: result.Status == "Complete"
            ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);

    private static IResult Report(PackageStoreMaintenanceResult result)
        => Results.Json(result, statusCode: result.Status switch
        {
            "Refused" => StatusCodes.Status409Conflict,
            "Failed" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status200OK
        });
}
