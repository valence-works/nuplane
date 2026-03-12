using Microsoft.Extensions.DependencyInjection;
using Nuplane.Operational;
using Nuplane.Reconciliation;

namespace Nuplane.Admin;

/// <summary>
/// Provides extension methods for registering optional in-process Nuplane admin services.
/// </summary>
public static class NuplaneAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the non-HTTP Nuplane admin surface, including operational snapshot projection
    /// and manual reconcile trigger coordination.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OperationalSnapshotProjector>();
        services.AddSingleton<ManualReconcileCoordinator>();
        services.AddSingleton<INuplaneAdminOperations, NuplaneAdminOperations>();

        return services;
    }
}
