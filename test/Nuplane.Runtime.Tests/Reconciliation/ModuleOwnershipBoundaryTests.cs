using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading;
using Nuplane.Loading.Registration;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Ownership boundary tests verifying that loading and directory module types
/// are registered through their module-owned registration services, not through core.
/// </summary>
public sealed class ModuleOwnershipBoundaryTests
{
    [Fact]
    public void LoadingRegistration_RegistersEventDispatcher()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services,
            d => d.ServiceType == typeof(ILoadingEventDispatcher));
    }

    [Fact]
    public void LoadingRegistration_RegistersLoadingFailureTracker()
    {
        var services = new ServiceCollection();

        LoadingRegistrationServices.Register(services);

        Assert.Contains(services,
            d => d.ServiceType == typeof(ILoadingFailureTracker));
    }
}
