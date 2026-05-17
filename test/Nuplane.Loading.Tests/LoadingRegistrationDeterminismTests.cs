using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Operational;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Loading.Registration;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Determinism tests verifying that repeated loading module registration
/// does not create duplicate services and follows last-registration-wins semantics.
/// </summary>
public sealed class LoadingRegistrationDeterminismTests
{
    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateCanonicalPublicServices()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IPackageAssemblyCatalog));
        Assert.Single(services, d => d.ServiceType == typeof(IPackageTypeFinder));
        Assert.Single(services, d => d.ServiceType == typeof(IPackageLoadStateCatalog));
    }

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicateModuleOwnedContributor()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IOperationalStateContributor));
    }

    [Fact]
    public void Register_CalledTwice_DoesNotReintroduceRemovedMechanicsInterfaces()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageLoader));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageAssemblyProvider));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageUnloadCoordinator));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingEventDispatcher));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingFailureTracker));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageTypeScanner));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingCatalog));
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

    [Fact]
    public void Register_CalledTwice_DoesNotDuplicatePackageLoadModeAdvisor()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);
        LoadingRegistrationServices.Register(services);

        Assert.Single(services, d => d.ServiceType == typeof(IPackageLoadModeAdvisor));
        Assert.Single(services, d => d.ServiceType == typeof(PackageMetadataLoadModeReader));
        Assert.Single(services, d => d.ServiceType == typeof(PackageMetadataLoadModeAdvisor));
    }

    [Fact]
    public void NuplaneLoadingBuilder_WithDefaultLoadMode_ConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var builder = new NuplaneLoadingBuilder(services);

        builder.WithDefaultLoadMode(PackageLoadMode.HostIntegrated);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;
        Assert.Equal(PackageLoadMode.HostIntegrated, options.DefaultLoadMode);
    }

    [Fact]
    public void NuplaneLoadingBuilder_WithLoadModeSelectionPolicy_ConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var builder = new NuplaneLoadingBuilder(services);

        builder.WithLoadModeSelectionPolicy(PackageLoadModeSelectionPolicy.ExplicitOnly);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LoadingOptions>>().Value;
        Assert.Equal(PackageLoadModeSelectionPolicy.ExplicitOnly, options.LoadModeSelectionPolicy);
    }

    [Fact]
    public void NuplaneLoadingBuilder_PackageLoadMode_ConfiguresPackageOverride()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        var builder = new NuplaneLoadingBuilder(services);

        builder.PackageLoadMode(" pkg-a ", PackageLoadMode.HostIntegrated);

        using var provider = services.BuildServiceProvider();
        var packageOverride = Assert.Single(provider.GetRequiredService<IOptions<LoadingOptions>>().Value.PackageLoadModes);
        Assert.Equal("pkg-a", packageOverride.PackageId);
        Assert.Equal(PackageLoadMode.HostIntegrated, packageOverride.LoadMode);
    }
}
