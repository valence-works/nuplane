using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Versioning;

namespace Nuplane.Registration;

internal static class NuplaneFeedVersioningRegistrationServices
{
    internal static void RegisterPolicyAndVersioning(this IServiceCollection services)
    {
        services.AddSingleton<FeedResolutionPolicy>();
        services.AddSingleton<NuGetFeedVersionEnumerator>();
        services.AddSingleton<IFeedVersionEnumerator>(sp =>
            new CachedFeedVersionEnumerator(
                sp.GetRequiredService<NuGetFeedVersionEnumerator>(),
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>()));
        services.AddSingleton<IVersionRangeEvaluator, NuGetVersionRangeEvaluator>();
    }

    internal static void RegisterPackageResolution(this IServiceCollection services)
    {
        services.AddSingleton<NuGetRemotePackageAcquirer>(sp => new NuGetRemotePackageAcquirer(
            sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
            sp.GetService<ISecretReferenceResolver>(),
            sp.GetRequiredService<PackageStoreCompositionSnapshot>()));
        services.AddSingleton<IRemotePackageAcquirer>(sp => sp.GetRequiredService<NuGetRemotePackageAcquirer>());
        services.AddSingleton<MultiFeedPackageResolver>(sp => new MultiFeedPackageResolver(
            sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
            sp.GetRequiredService<FeedResolutionPolicy>(),
            sp.GetRequiredService<IRemotePackageAcquirer>(),
            sp.GetRequiredService<IFeedVersionEnumerator>(),
            sp.GetRequiredService<IVersionRangeEvaluator>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MultiFeedPackageResolver>>(),
            sp.GetService<Nuplane.Observability.ReconciliationMetrics>(),
            sp.GetRequiredService<PackageStoreCompositionSnapshot>()));
        services.AddSingleton<IPackageResolver>(sp => sp.GetRequiredService<MultiFeedPackageResolver>());
    }
}
