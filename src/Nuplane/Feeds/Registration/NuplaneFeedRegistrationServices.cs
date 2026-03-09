using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Sources.Directory;
using Nuplane.Sources.Directory.Registration;

namespace Nuplane.Feeds.Registration;

internal static class NuplaneFeedRegistrationServices
{
    internal static bool HasRegisteredFeed(IServiceCollection services, string name) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(NuplaneFeedRegistration)
            && descriptor.ImplementationInstance is NuplaneFeedRegistration registration
            && string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static void AddRegistrationMarker(IServiceCollection services, NuplaneFeedBuilder feed) =>
        services.AddSingleton(new NuplaneFeedRegistration(
            feed.Name,
            DistinctNonBlank(feed.IncludePatterns).ToArray(),
            HasExplicitUnrestrictedPackageSelection(feed)));

    internal static void Register(IServiceCollection services, NuplaneFeedBuilder feed)
    {
        if (feed.DirectoryOptions is { } dirOpts)
        {
            DirectorySourceRegistrationServices.RegisterFeed(
                services,
                feed.Name,
                dirOpts,
                feed.IncludePatterns,
                feed.TrustLevel,
                feed.Credentials);

            return;
        }

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

    internal static void ConfigureSourceTrustOptions(IServiceCollection services)
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
