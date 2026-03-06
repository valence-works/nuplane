using Microsoft.Extensions.DependencyInjection;
using Nuplane.Contracts;
using Nuplane.Operational;
using Nuplane.Runtime.Operational;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Admin.Api;

/// <summary>
/// Provides extension methods for registering optional Nuplane admin services.
/// </summary>
public static class NuplaneAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process Nuplane admin surface used by <see cref="NuplaneAdminEndpointExtensions"/>,
    /// including operational snapshot projection and manual reconcile trigger coordination.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        NuplaneServiceCollectionExtensions.EnsureTriggerIngressServices(services);
        services.AddSingleton<OperationalSnapshotProjector>();
        services.AddSingleton<ManualReconcileCoordinator>();
        services.AddSingleton<INuplaneAdminOperations, NuplaneAdminOperations>();

        return services;
    }
}
