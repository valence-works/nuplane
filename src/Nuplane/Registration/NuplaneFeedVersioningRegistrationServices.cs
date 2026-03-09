using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.NuGet;
using Nuplane.Runtime.Feeds;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Feeds.Versioning;
using Nuplane.Runtime.Observability;

namespace Nuplane.Registration;

internal static class NuplaneFeedVersioningRegistrationServices
{
    internal static void RegisterPolicyAndVersioning(IServiceCollection services)
    {
        services.AddSingleton<FeedResolutionPolicy>();
        services.AddSingleton<NuGetFeedVersionEnumerator>();
        services.AddSingleton<IFeedVersionEnumerator>(sp =>
            new CachedFeedVersionEnumerator(
                sp.GetRequiredService<NuGetFeedVersionEnumerator>(),
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>()));
        services.AddSingleton<IVersionRangeEvaluator, NuGetVersionRangeEvaluator>();
        services.AddSingleton<FeedTrustPolicyEvaluator>();
        services.AddSingleton<IFeedTrustPolicyEvaluator>(sp => sp.GetRequiredService<FeedTrustPolicyEvaluator>());
        services.AddSingleton<RestrictedFeedValidatorPipeline>();
        services.AddSingleton<UntrustedOverridePolicy>();
    }

    internal static void RegisterPackageResolution(IServiceCollection services)
    {
        services.AddSingleton<IRemotePackageAcquirer>(sp =>
            new NuGetRemotePackageAcquirer(sp.GetRequiredService<IOptions<FeedResolutionOptions>>()));
        services.AddSingleton<IPackageResolver>(sp =>
            new MultiFeedPackageResolver(
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
                sp.GetRequiredService<FeedResolutionPolicy>(),
                sp.GetRequiredService<IRemotePackageAcquirer>(),
                sp.GetRequiredService<IFeedVersionEnumerator>(),
                sp.GetRequiredService<IVersionRangeEvaluator>(),
                sp.GetService<ILogger<MultiFeedPackageResolver>>() ?? NullLogger<MultiFeedPackageResolver>.Instance,
                sp.GetRequiredService<ReconciliationMetrics>()));
    }
}
