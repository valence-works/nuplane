using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Store.Coordination;

/// <summary>A configured package-store root locator in the host's trusted composition.</summary>
internal sealed record TrustedPackageStoreRoot(string Label, string RootPath);

/// <summary>The immutable root catalog available to later store-coordination services.</summary>
internal interface ITrustedPackageStoreRootCatalog
{
    IReadOnlyList<TrustedPackageStoreRoot> Roots { get; }
}

/// <summary>A root locator collected by the builder and anchored at callback finalization.</summary>
internal sealed record TrustedPackageStoreRootRegistration(string Label, string RootLocator);

/// <summary>The immutable configured-root definitions captured by one successful builder callback.</summary>
internal interface ITrustedPackageStoreRootCatalogDefinition
{
    IReadOnlyList<TrustedPackageStoreRootRegistration> AdditionalRoots { get; }
}

/// <summary>An immutable snapshot of the additional roots configured by successful AddNuplane calls.</summary>
internal sealed class TrustedPackageStoreRootCatalogDefinition : ITrustedPackageStoreRootCatalogDefinition
{
    public TrustedPackageStoreRootCatalogDefinition(IEnumerable<TrustedPackageStoreRootRegistration> additionalRoots)
    {
        ArgumentNullException.ThrowIfNull(additionalRoots);
        AdditionalRoots = Array.AsReadOnly(additionalRoots.ToArray());
    }

    public IReadOnlyList<TrustedPackageStoreRootRegistration> AdditionalRoots { get; }

    internal static TrustedPackageStoreRootCatalogDefinition? From(IServiceCollection services)
        => services.LastOrDefault(static descriptor =>
                descriptor.ServiceType == typeof(TrustedPackageStoreRootCatalogDefinition))
            ?.ImplementationInstance as TrustedPackageStoreRootCatalogDefinition;
}

/// <summary>Resolves the default root lazily from final feed options and combines it with frozen locators.</summary>
internal sealed class TrustedPackageStoreRootCatalog : ITrustedPackageStoreRootCatalog
{
    private readonly Lazy<IReadOnlyList<TrustedPackageStoreRoot>> _roots;

    public TrustedPackageStoreRootCatalog(
        ITrustedPackageStoreRootCatalogDefinition definition,
        IOptions<FeedResolutionOptions> feedOptions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(feedOptions);

        var additionalRoots = definition.AdditionalRoots.ToArray();
        _roots = new(() =>
        {
            var roots = new TrustedPackageStoreRoot[additionalRoots.Length + 1];
            roots[0] = new("default", PackageInstallStore.ResolveInstallRoot(feedOptions.Value));
            for (var index = 0; index < additionalRoots.Length; index++)
            {
                var root = additionalRoots[index];
                roots[index + 1] = new(root.Label, root.RootLocator);
            }

            return Array.AsReadOnly(roots);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<TrustedPackageStoreRoot> Roots => _roots.Value;
}

internal static class TrustedPackageStoreRootCatalogRegistration
{
    internal static void Register(
        IServiceCollection services,
        TrustedPackageStoreRootCatalogDefinition definition)
    {
        services.Replace(ServiceDescriptor.Singleton<TrustedPackageStoreRootCatalogDefinition>(definition));
        services.Replace(ServiceDescriptor.Singleton<ITrustedPackageStoreRootCatalogDefinition>(provider =>
            provider.GetRequiredService<TrustedPackageStoreRootCatalogDefinition>()));
        services.Replace(ServiceDescriptor.Singleton<TrustedPackageStoreRootCatalog>(provider =>
            new(
                provider.GetRequiredService<ITrustedPackageStoreRootCatalogDefinition>(),
                provider.GetRequiredService<IOptions<FeedResolutionOptions>>())));
        services.Replace(ServiceDescriptor.Singleton<ITrustedPackageStoreRootCatalog>(provider =>
            provider.GetRequiredService<TrustedPackageStoreRootCatalog>()));
    }
}
