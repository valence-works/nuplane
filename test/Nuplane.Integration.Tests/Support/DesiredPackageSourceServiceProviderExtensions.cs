using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Sources;

namespace Nuplane.Integration.Tests.Support;

/// <summary>
/// Test helpers for resolving <see cref="IDesiredPackageSource"/> registrations from a built
/// <see cref="IServiceProvider"/>.
/// </summary>
internal static class DesiredPackageSourceServiceProviderExtensions
{
    /// <summary>
    /// Gets the registered <see cref="IDesiredPackageSource"/> instances that originate from feed
    /// registrations. <see cref="DesiredManifestPackageSource"/> is always registered (gated at
    /// runtime by <c>ConvergenceOptions.Manifest.Enabled</c>); it is excluded here to isolate
    /// feed-derived sources.
    /// </summary>
    public static IEnumerable<IDesiredPackageSource> GetFeedDesiredPackageSources(this IServiceProvider provider) =>
        provider.GetServices<IDesiredPackageSource>()
            .Where(source => source is not DesiredManifestPackageSource);
}
