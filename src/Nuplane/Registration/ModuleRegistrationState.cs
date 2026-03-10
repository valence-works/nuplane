using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Nuplane.Registration;

/// <summary>
/// Shared helpers for deterministic last-registration-wins module replacement semantics.
/// Module registration services use these helpers to ensure re-registration replaces
/// prior module state without leaving duplicate hosted services, observers, or options consumers.
/// </summary>
public static class ModuleRegistrationState
{
    /// <summary>
    /// Replaces all previously registered singleton services of the specified type,
    /// then adds the new implementation. This ensures last-registration-wins semantics
    /// for module-owned singletons.
    /// </summary>
    public static void ReplaceSingleton<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        RemoveAll<TService>(services);
        services.AddSingleton<TService, TImplementation>();
    }

    /// <summary>
    /// Replaces all previously registered singleton services of the specified type with a
    /// factory-based registration. This ensures last-registration-wins semantics.
    /// </summary>
    public static void ReplaceSingleton<TService>(
        IServiceCollection services,
        Func<IServiceProvider, TService> factory)
        where TService : class
    {
        RemoveAll<TService>(services);
        services.AddSingleton(factory);
    }

    /// <summary>
    /// Replaces all previously registered hosted service implementations of the specified type.
    /// Removes any existing <see cref="IHostedService"/> registrations that resolve to
    /// <typeparamref name="TImplementation"/>, then adds a single new registration.
    /// </summary>
    public static void ReplaceHostedService<TImplementation>(
        IServiceCollection services,
        Func<IServiceProvider, TImplementation> factory)
        where TImplementation : class, IHostedService
    {
        // Remove existing registrations for this specific hosted service type.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IHostedService))
                continue;

            if (descriptor.ImplementationType == typeof(TImplementation)
                || descriptor.ImplementationFactory?.Method.ReturnType == typeof(TImplementation))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton<IHostedService>(factory);
    }

    /// <summary>
    /// Ensures a singleton service is registered exactly once using try-add semantics.
    /// Prefer this when the first registration should win (e.g., shared infrastructure).
    /// </summary>
    public static void TryAddSingleton<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        services.TryAddSingleton<TService, TImplementation>();
    }

    private static void RemoveAll<TService>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService))
            {
                services.RemoveAt(i);
            }
        }
    }
}
