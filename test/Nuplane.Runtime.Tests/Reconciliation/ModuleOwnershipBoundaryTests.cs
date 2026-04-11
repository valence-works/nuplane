using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading;
using Nuplane.Loading.Registration;
using Nuplane.Operational;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Ownership boundary tests verifying that loading and directory module types
/// are registered through their module-owned registration services, not through core.
/// </summary>
public sealed class ModuleOwnershipBoundaryTests
{
    [Fact]
    public void LoadingRegistration_RegistersCanonicalModuleServicesWithoutRetiredMechanicsInterfaces()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

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
    }

    [Fact]
    public void CoreRuntimeRegistration_DoesNotExposeRetiredLoadingContractsPublicly()
    {
        var exportedTypeNames = typeof(IPackageAssemblyCatalog).Assembly
            .GetExportedTypes()
            .Select(static type => type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(nameof(IPackageLoader), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageAssemblyProvider), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageUnloadCoordinator), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingEventDispatcher), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingFailureTracker), exportedTypeNames);
        Assert.DoesNotContain(nameof(IPackageTypeScanner), exportedTypeNames);
        Assert.DoesNotContain(nameof(ILoadingCatalog), exportedTypeNames);
    }
}
