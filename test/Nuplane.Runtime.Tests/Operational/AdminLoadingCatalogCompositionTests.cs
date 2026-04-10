using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;
using Nuplane.Loading;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class AdminLoadingCatalogCompositionTests
{
    [Fact]
    public void AdminContract_RemainsLoadingFree()
    {
        var methodNames = typeof(INuplaneAdminOperations)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GetPackagesAsync", "GetStateAsync", "TriggerReconcileAsync"], methodNames);
    }

    [Fact]
    public void AddNuplaneAdmin_RegistersAdminOperationsWithoutLoadingServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<INuplaneAdminOperations>());
        Assert.Null(provider.GetService<ILoadingCatalog>());
    }
}

