using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;

namespace Nuplane.Tests.Shared;

internal static class ProtectedReconciliationServices
{
    // Exercise participant checks at the runtime boundary with the production pipeline composition.
    internal static ReconciliationService Create(IServiceProvider services)
    {
        var loadingObserver = services.GetService<ILeasedPackageGraphLoadingObserver>();
        return new(ReconciliationComposition.FromServices(
            services,
            services.GetRequiredService<IPackageResolver>(),
            services.GetRequiredService<IPackageStoreAdmission>(),
            storeComposition: null,
            loadingObserver));
    }
}
