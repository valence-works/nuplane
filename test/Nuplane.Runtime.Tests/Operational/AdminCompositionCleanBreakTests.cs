using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class AdminCompositionCleanBreakTests
{
    [Fact]
    public void AdminContract_DoesNotExposeLoadingNamespacesInPublicSurface()
    {
        var methods = typeof(INuplaneAdminOperations).GetMethods();

        Assert.All(methods, method =>
        {
            Assert.False(IsLoadingType(method.ReturnType), $"Method '{method.Name}' exposes loading return type '{method.ReturnType}'.");
            Assert.All(method.GetParameters(), parameter =>
                Assert.False(IsLoadingType(parameter.ParameterType), $"Method '{method.Name}' exposes loading parameter type '{parameter.ParameterType}'."));
        });
    }

    [Fact]
    public async Task AddNuplaneAdmin_ComposesPackageAndStateReadsWithoutLoadingModule()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        services.AddNuplaneAdmin();

        using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<INuplaneAdminOperations>();

        var packages = await operations.GetPackagesAsync(CancellationToken.None);
        var state = await operations.GetStateAsync(CancellationToken.None);

        Assert.Empty(packages.Packages);
        Assert.Empty(state.DegradedReasons);
    }

    private static bool IsLoadingType(Type type)
    {
        if (type.Namespace?.StartsWith("Nuplane.Loading", StringComparison.Ordinal) == true)
        {
            return true;
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsLoadingType);
        }

        return false;
    }
}

