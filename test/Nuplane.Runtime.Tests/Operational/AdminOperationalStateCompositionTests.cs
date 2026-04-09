using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;
using Nuplane.Operational;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class AdminOperationalStateCompositionTests
{
    [Fact]
    public async Task GetStateAsync_ComposesStateOnlyOperationalSnapshot()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<INuplaneAdminOperations>();

        var snapshot = await operations.GetStateAsync(CancellationToken.None);

        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.Empty(snapshot.DegradedReasons);
    }
}

