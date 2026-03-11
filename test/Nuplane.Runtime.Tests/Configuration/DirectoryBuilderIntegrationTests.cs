using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Runtime.Tests.Configuration;

/// <summary>
/// Contract tests for the module-owned <see cref="NuplaneBuilderDirectoryExtensions.AddDirectoryFeed"/>
/// builder extension verifying it registers the expected directory source services and feed
/// registrations through the hosting builder API.
/// </summary>
public sealed class DirectoryBuilderIntegrationTests
{
    [Fact]
    public void AddDirectoryFeed_RegistersDesiredPackageSource()
    {
        var root = CreateTempDir("builder-source");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("drop-folder", root, feed =>
                {
                    feed.IncludeAll();
                });
            });

            Assert.Contains(services, d => d.ServiceType == typeof(IDesiredPackageSource));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_RegistersSourceTrustForFeed()
    {
        var root = CreateTempDir("builder-trust");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("trusted-dir", root, feed =>
                {
                    feed.Include("Acme.*");
                });
            });

            using var provider = services.BuildServiceProvider();
            var trust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Contains("trusted-dir", trust.AllowedSourceNames);
            Assert.Contains("Acme.*", trust.AllowedPackageIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_WithWatch_RegistersHostedService()
    {
        var root = CreateTempDir("builder-watch");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("watched", root, feed =>
                {
                    feed.Watch = true;
                    feed.IncludeAll();
                });
            });

            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_WithoutWatch_DoesNotRegisterDirectoryHostedService()
    {
        var root = CreateTempDir("builder-no-watch");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("unwatched", root, feed =>
                {
                    feed.Watch = false;
                    feed.IncludeAll();
                });
            });

            // Filter out core hosted services (ReconciliationHostedService, etc.)
            var directoryHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .Where(d => d.ImplementationType is null) // Directory ones use factory lambdas
                .ToList();

            Assert.Empty(directoryHostedServices);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_IncludeAll_CollapsesPackageAllowlistToWildcard()
    {
        var root = CreateTempDir("builder-include-all");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("wildcard-feed", root, feed =>
                {
                    feed.IncludeAll();
                });
            });

            using var provider = services.BuildServiceProvider();
            var trust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Single(trust.AllowedPackageIds);
            Assert.Contains("*", trust.AllowedPackageIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_ReRegistration_ReplacesEarlierFeed()
    {
        var root = CreateTempDir("builder-re-register");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("re-feed", root, feed =>
                {
                    feed.Watch = true;
                    feed.Include("Old.*");
                });

                // Re-register same feed with different options
                nuplane.AddDirectoryFeed("re-feed", root, feed =>
                {
                    feed.Watch = false;
                    feed.Include("New.*");
                });
            });

            // Only one desired source for the feed
            var sourceDescriptors = services
                .Where(d => d.ServiceType == typeof(IDesiredPackageSource))
                .ToList();
            Assert.Single(sourceDescriptors);

            using var provider = services.BuildServiceProvider();
            var trust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Contains("New.*", trust.AllowedPackageIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateTempDir(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nuplane-dir-builder-{suffix}", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch
        {
            // ignored
        }
    }
}
