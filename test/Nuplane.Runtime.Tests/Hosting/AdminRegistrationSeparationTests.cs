using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin.Api;
using Nuplane.Contracts;

namespace Nuplane.Runtime.Tests.Hosting;

public sealed class AdminRegistrationSeparationTests
{
    [Fact]
    public void AddNuplane_DoesNotRegisterAdminOperationsByDefault()
    {
        var services = new ServiceCollection();

        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<INuplaneAdminOperations>());
    }

    [Fact]
    public void AddNuplaneAdmin_RegistersAdminOperationsWhenOptedIn()
    {
        var services = new ServiceCollection();

        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<INuplaneAdminOperations>());
    }
}

