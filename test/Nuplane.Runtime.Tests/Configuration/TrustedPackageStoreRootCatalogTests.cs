using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Restore;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class TrustedPackageStoreRootCatalogTests
{
    [Fact]
    public void AddNuplane_AnchorsAdditionalRootsAtFinalBaseAndResolvesDefaultFromFinalOptions()
    {
        using var temp = new TempDirectory();
        var installRoot = Path.Combine(temp.Path, "late-install-root");
        var absoluteRoot = Path.Combine(temp.Path, "absolute-root");
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = "early-root");
        services.AddNuplane(builder =>
        {
            builder.AddPackageStoreRoot("relative", "relative-root");
            builder.AddPackageStoreRoot("absolute", absoluteRoot);
            builder.AddPackageStoreRoot("same-absolute-root", absoluteRoot);
            builder.UseBasePath(temp.Path);
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = installRoot);

        using var provider = services.BuildServiceProvider();
        var roots = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;

        Assert.Collection(roots,
            root =>
            {
                Assert.Equal("default", root.Label);
                Assert.Equal(Path.GetFullPath(installRoot), root.RootPath);
            },
            root =>
            {
                Assert.Equal("relative", root.Label);
                Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "relative-root")), root.RootPath);
            },
            root =>
            {
                Assert.Equal("absolute", root.Label);
                Assert.Equal(Path.GetFullPath(absoluteRoot), root.RootPath);
            },
            root =>
            {
                Assert.Equal("same-absolute-root", root.Label);
                Assert.Equal(Path.GetFullPath(absoluteRoot), root.RootPath);
            });

        Assert.False(Directory.Exists(installRoot));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "relative-root")));
        Assert.False(Directory.Exists(absoluteRoot));
    }

    [Fact]
    public void AddNuplane_WithoutBasePathFreezesCurrentDirectoryAndKeepsDefaultFallback()
    {
        using var temp = new TempDirectory();
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var configuredDirectory = Path.Combine(temp.Path, "configured-cwd");
        var laterDirectory = Path.Combine(temp.Path, "later-cwd");
        Directory.CreateDirectory(configuredDirectory);
        Directory.CreateDirectory(laterDirectory);
        var services = new ServiceCollection();
        services.AddLogging();

        try
        {
            Environment.CurrentDirectory = configuredDirectory;
            services.AddNuplane(builder => builder.AddPackageStoreRoot("relative", "packages"));
            Environment.CurrentDirectory = laterDirectory;
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }

        using var provider = services.BuildServiceProvider();
        var roots = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages"),
            Assert.Single(roots, static root => root.Label == "default").RootPath);
        Assert.Equal(Path.Combine(configuredDirectory, "packages"),
            Assert.Single(roots, static root => root.Label == "relative").RootPath);
        Assert.False(Directory.Exists(Path.Combine(configuredDirectory, "packages")));
    }

    [Fact]
    public void AddNuplane_RejectsInvalidLabelsAndDuplicatesAcrossCalls()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.AddPackageStoreRoot("first", "one"));

        Assert.Throws<ArgumentException>(() => services.AddNuplane(builder => builder.AddPackageStoreRoot(" ", "root")));
        Assert.Throws<ArgumentException>(() => services.AddNuplane(builder => builder.AddPackageStoreRoot(" padded ", "root")));
        Assert.Throws<ArgumentException>(() => services.AddNuplane(builder => builder.AddPackageStoreRoot("label", " ")));
        Assert.Throws<InvalidOperationException>(() => services.AddNuplane(builder => builder.AddPackageStoreRoot("DEFAULT", "root")));
        Assert.Throws<InvalidOperationException>(() => services.AddNuplane(builder => builder.AddPackageStoreRoot("FIRST", "another")));
    }

    [Fact]
    public void AddNuplane_FinalizesImmutableCatalogAndRetainsRootsAcrossRepeatedCalls()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        NuplaneBuilder? capturedBuilder = null;
        services.AddNuplane(builder =>
        {
            capturedBuilder = builder;
            builder.AddPackageStoreRoot("first", "first-root");
        });

        using var firstProvider = services.BuildServiceProvider();
        var firstCatalog = firstProvider.GetRequiredService<ITrustedPackageStoreRootCatalog>();
        var frozenFirstRoots = firstCatalog.Roots;
        capturedBuilder!.AddPackageStoreRoot("late-builder-mutation", "late-root");

        Assert.Equal(2, frozenFirstRoots.Count);
        Assert.DoesNotContain(frozenFirstRoots, static root => root.Label == "late-builder-mutation");

        services.AddNuplane(builder => builder.AddPackageStoreRoot("second", "second-root"));
        using var secondProvider = services.BuildServiceProvider();
        var secondRoots = secondProvider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;

        Assert.Collection(secondRoots,
            root => Assert.Equal("default", root.Label),
            root => Assert.Equal("first", root.Label),
            root => Assert.Equal("second", root.Label));
        Assert.Equal(2, frozenFirstRoots.Count);
        Assert.Same(firstCatalog, firstProvider.GetRequiredService<ITrustedPackageStoreRootCatalog>());
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services.Where(static descriptor =>
            descriptor.ServiceType == typeof(ITrustedPackageStoreRootCatalog))).Lifetime);

        // Resolving the catalog, registry, and current admission in one real AddNuplane provider
        // proves this service remains a leaf and does not introduce a composition cycle.
        _ = secondProvider.GetRequiredService<IStoreRegistry>();
        _ = secondProvider.GetRequiredService<IPackageStoreAdmission>();
    }

    [Fact]
    public async Task RestoreComposition_UsesLateResolvedDefaultAndAnchorsBuilderRootToRestoreBase()
    {
        using var temp = new TempDirectory();
        var configuredInstallRoot = "late-configured-install-root";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Setup:Feeds:offline:ServiceIndex"] = "https://packages.example.test/v3/index.json"
            })
            .Build();
        var options = new NuplaneRestoreOptions
        {
            BasePath = temp.Path,
            ConfigureBuilder = (builder, _) =>
            {
                builder.AddPackageStoreRoot("restore-extra", "extra-root");
                builder.Services.Configure<FeedResolutionOptions>(feedOptions =>
                    feedOptions.PackageInstallRoot = configuredInstallRoot);
            }
        };

        await using var composition = await RestoreComposition.Create(configuration, options);
        var roots = composition.Services.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;
        _ = composition.Services.GetRequiredService<IStoreRegistry>();
        _ = composition.Services.GetRequiredService<IPackageStoreAdmission>();

        Assert.Equal(Path.Combine(temp.Path, configuredInstallRoot), composition.InstallRoot);
        Assert.Collection(roots,
            root =>
            {
                Assert.Equal("default", root.Label);
                Assert.Equal(composition.InstallRoot, root.RootPath);
            },
            root =>
            {
                Assert.Equal("restore-extra", root.Label);
                Assert.Equal(Path.Combine(temp.Path, "extra-root"), root.RootPath);
            });
        Assert.False(Directory.Exists(composition.InstallRoot));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "extra-root")));
    }
}
