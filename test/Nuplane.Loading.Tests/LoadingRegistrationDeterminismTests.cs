using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Loading.Registration;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Determinism tests verifying that repeated loading module registration
/// does not create duplicate services and follows last-registration-wins semantics.
/// </summary>
public sealed class LoadingRegistrationDeterminismTests
{
    [Fact]
    public void Register_CalledTwice_DoesNotDuplicatePackageLoader()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IPackageLoader));
    }

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateTypeScanner()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IPackageTypeScanner));
    }

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateUnloadCoordinator()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IPackageUnloadCoordinator));
    }

    [Fact]
    public void Register_CalledTwice_ReplacesEventDispatcher()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(ILoadingEventDispatcher));
    }

    [Fact]
    public void Register_CalledTwice_ReplacesFailureTracker()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(ILoadingFailureTracker));
    }

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateOptionsValidation()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        var validatorCount = services.Count(d =>
            d.ServiceType == typeof(IValidateOptions<LoadingOptions>));

        Assert.Equal(1, validatorCount);
    }

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateSharedAssemblyPolicyMatcher()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(SharedAssemblyPolicyMatcher));
    }
}
