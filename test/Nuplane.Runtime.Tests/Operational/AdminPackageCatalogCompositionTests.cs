using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class AdminPackageCatalogCompositionTests
{
    [Fact]
    public async Task GetPackagesAsync_ComposesStandaloneActiveCatalog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<INuplaneAdminOperations>();

        var snapshot = await operations.GetPackagesAsync(CancellationToken.None);

        Assert.Empty(snapshot.Packages);
    }
}

