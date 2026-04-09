using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading.Api;
using Nuplane.Operational;
using Nuplane.Loading.Registration;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Contract tests verifying that loading module owns its options, validators,
/// and registration services. These validate the ownership split from <c>Loading.Abstractions</c>
/// and <c>Loading.Hosting</c> into the <c>Loading</c> implementation package.
/// </summary>
public sealed class LoadingOwnershipContractTests
{
    [Fact]
    public void Register_RegistersLoadingOptions()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(LoadingOptionsValidator));
    }

    [Fact]
    public void Register_RegistersPackageLoader()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoader));
    }

    [Fact]
    public void Register_RegistersLoadingEventDispatcher()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(ILoadingEventDispatcher));
    }

    [Fact]
    public void Register_RegistersLoadingFailureTracker()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(ILoadingFailureTracker));
    }

    [Fact]
    public void Register_RegistersLoadingOperationalStateContributor()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IOperationalStateContributor));
    }

    [Fact]
    public void MapNuplaneLoading_ExtensionExistsInLoadingOwnedApiPackage()
    {
        var method = typeof(NuplaneLoadingEndpointExtensions).GetMethod(nameof(NuplaneLoadingEndpointExtensions.MapNuplaneLoading));

        Assert.NotNull(method);
    }
}
