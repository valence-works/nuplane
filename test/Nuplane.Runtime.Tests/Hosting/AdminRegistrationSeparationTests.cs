using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;

namespace Nuplane.Runtime.Tests.Hosting;

public sealed class AdminRegistrationSeparationTests
{
    [Fact]
    public void AddNuplane_DoesNotRegisterAdminOperationsByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<INuplaneAdminOperations>());
        Assert.Null(provider.GetService<INuplanePackageStoreMaintenanceOperations>());
    }

    [Fact]
    public void AddNuplaneAdmin_RegistersAdminOperationsWhenOptedIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<INuplaneAdminOperations>());
        Assert.Null(provider.GetService<INuplanePackageStoreMaintenanceOperations>());
    }

    [Fact]
    public void AddNuplanePackageStoreMaintenance_IsExplicitAndDoesNotReplaceLegacyAdmin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();
        services.AddNuplanePackageStoreMaintenance();
        services.AddNuplanePackageStoreMaintenance();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<INuplaneAdminOperations>());
        Assert.Same(provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>(),
            provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>());
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(INuplanePackageStoreMaintenanceOperations));
    }
}
