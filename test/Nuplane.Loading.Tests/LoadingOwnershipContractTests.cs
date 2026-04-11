using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading.Api;
using Nuplane.Operational;
using Nuplane.Loading.Registration;
using System.Reflection;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Contract tests verifying that loading module owns its options, validators,
/// and registration services. These validate the ownership split from <c>Loading.Abstractions</c>
/// and <c>Loading.Hosting</c> into the <c>Loading</c> implementation package.
/// </summary>
public sealed class LoadingOwnershipContractTests
{
    [Fact]
    public void PublicLoadingAbstractions_ExposeOnlyCanonicalHostFacingContracts()
    {
        var exportedTypeNames = typeof(IPackageAssemblyCatalog).Assembly
            .GetExportedTypes()
            .Select(static type => type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(nameof(IPackageAssemblyCatalog), exportedTypeNames);
        Assert.Contains(nameof(IPackageTypeFinder), exportedTypeNames);
        Assert.Contains(nameof(IPackageLoadStateCatalog), exportedTypeNames);
        Assert.Contains(nameof(PackageLoadStateSnapshot), exportedTypeNames);
        Assert.Contains(nameof(PackageLoadState), exportedTypeNames);
        Assert.Contains(nameof(PackageAssemblyReference), exportedTypeNames);
        Assert.Contains(nameof(PackageAssemblies), exportedTypeNames);

        Assert.DoesNotContain(nameof(IPackageLoader), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageAssemblyProvider), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageUnloadCoordinator), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingEventDispatcher), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingFailureTracker), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageLoadingObserver), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageTypeScanner), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingCatalog), exportedTypeNames);
        Assert.DoesNotContain(nameof(LoadingCatalogSnapshot), exportedTypeNames);
        Assert.DoesNotContain(nameof(LoadingPackageDescriptor), exportedTypeNames);
        Assert.DoesNotContain(nameof(LoadingCatalogAvailability), exportedTypeNames);
        Assert.DoesNotContain(nameof(LoadingStatus), exportedTypeNames);
        Assert.DoesNotContain(nameof(AssemblyScanCandidate), exportedTypeNames);
        Assert.DoesNotContain(nameof(PackageLoadSession), exportedTypeNames);
        Assert.DoesNotContain(nameof(PackageLoadContextHandle), exportedTypeNames);
        Assert.DoesNotContain(nameof(PackageLoadResult), exportedTypeNames);
        Assert.DoesNotContain(nameof(DeactivationAttempt), exportedTypeNames);
        Assert.DoesNotContain(nameof(UnloadOutcome), exportedTypeNames);
        Assert.DoesNotContain(nameof(UnloadOutcomeRecord), exportedTypeNames);
    }

    [Fact]
    public void Register_RegistersOnlySurvivingPublicServices_AndModuleOwnedContributor()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services, d => d.ServiceType == typeof(LoadingOptionsValidator));
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageAssemblyCatalog));
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageTypeFinder));
        Assert.Contains(services, d => d.ServiceType == typeof(IPackageLoadStateCatalog));
        Assert.Contains(services, d => d.ServiceType == typeof(IOperationalStateContributor));

        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageLoader));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageAssemblyProvider));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageUnloadCoordinator));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingEventDispatcher));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingFailureTracker));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(IPackageTypeScanner));
        Assert.DoesNotContain(services, d => d.ServiceType.Name == nameof(ILoadingCatalog));
    }

    [Fact]
    public void LoadingOwnedApi_ExposesLoadStateEndpointOnly()
    {
        var methods = typeof(NuplaneLoadStateEndpointExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains("MapNuplaneLoadState", methods);
        Assert.DoesNotContain("MapNuplaneLoading", methods);
    }
}
