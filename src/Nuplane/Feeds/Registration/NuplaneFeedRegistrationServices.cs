using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Feeds.Builder;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust.Source;

namespace Nuplane.Feeds.Registration;

/// <summary>
/// Provides services for registering Nuplane feeds and their associated metadata in the service collection.
/// </summary>
public static class NuplaneFeedRegistrationServices
{
    /// <summary>
    /// Determines whether a feed with the specified name has already been registered in the service collection.
    /// </summary>
    /// <param name="services">The service collection where feeds are registered.</param>
    /// <param name="name">The name of the feed to check for registration.</param>
    /// <returns>True if the feed is registered; otherwise, false.</returns>
    public static bool HasRegisteredFeed(IServiceCollection services, string name) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(NuplaneFeedRegistration)
            && descriptor.ImplementationInstance is NuplaneFeedRegistration registration
            && string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a registration marker for the specified feed to the service collection. This marker is used to track registered feeds
    /// </summary>
    public static void AddRegistrationMarker(IServiceCollection services, NuplaneFeedBuilder feed) =>
        services.AddSingleton(new NuplaneFeedRegistration(
            feed.Name,
            DistinctNonBlank(feed.IncludePatterns).ToArray(),
            HasExplicitUnrestrictedPackageSelection(feed)));

    /// <summary>
    /// Registers the feed defined by the <see cref="NuplaneFeedBuilder"/> in the service collection, including its resolution options and desired package source if applicable.
    /// </summary>
    public static void Register(IServiceCollection services, NuplaneFeedBuilder feed)
    {
        if (feed.ServiceIndex is { } serviceIndex)
        {
            services.PostConfigure<FeedResolutionOptions>(opts =>
            {
                if (!opts.Feeds.Any(f => string.Equals(f.Name, feed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Feeds.Add(new(feed.Name, serviceIndex, feed.TrustLevel, feed.Credentials));
                }
            });

            var patterns = DistinctNonBlank(feed.IncludePatterns).ToArray();
            if (patterns.Length > 0)
            {
                var capturedFeedName = feed.Name;
                services.AddSingleton<IDesiredPackageSource>(
                    _ => new FeedRuleDesiredSource(capturedFeedName, patterns));
            }
        }
    }

    /// <summary>
    /// Configures the <see cref="SourceTrustOptions"/> based on the registered feeds and their package selection patterns. If any feed has an explicit unrestricted package selection (i.e., includes "*"), all package ID restrictions are cleared and all sources are allowed. Otherwise, the allowed package ID patterns from all feeds are aggregated into the options.
    /// </summary>
    public static void ConfigureSourceTrustOptions(IServiceCollection services)
    {
        var registrations = services
            .Where(static descriptor => descriptor.ServiceType == typeof(NuplaneFeedRegistration))
            .Select(static descriptor => descriptor.ImplementationInstance)
            .OfType<NuplaneFeedRegistration>()
            .ToArray();

        if (registrations.Length == 0)
        {
            return;
        }

        var hasExplicitUnrestrictedFeed = registrations.Any(static registration => registration.HasExplicitUnrestrictedPackageSelection);
        var allIncludePatterns = registrations
            .SelectMany(static registration => registration.IncludePatterns)
            .ToArray();

        services.Configure<SourceTrustOptions>(opts =>
        {
            foreach (var registration in registrations)
            {
                opts.AllowedSourceNames.Add(registration.Name);
            }

            if (hasExplicitUnrestrictedFeed)
            {
                opts.AllowedPackageIds.Clear();
                opts.AllowedPackageIds.Add("*");
                return;
            }

            foreach (var pattern in allIncludePatterns)
            {
                opts.AllowedPackageIds.Add(pattern);
            }
        });
    }

    private static bool HasExplicitUnrestrictedPackageSelection(NuplaneFeedBuilder feed) =>
        feed.IncludePatterns.Any(static pattern => string.Equals(pattern, "*", StringComparison.Ordinal));

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
