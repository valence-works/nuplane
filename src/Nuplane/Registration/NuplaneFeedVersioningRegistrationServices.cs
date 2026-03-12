using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
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
        services.AddSingleton<IRemotePackageAcquirer, NuGetRemotePackageAcquirer>();
        services.AddSingleton<IPackageResolver, MultiFeedPackageResolver>();
    }
}
