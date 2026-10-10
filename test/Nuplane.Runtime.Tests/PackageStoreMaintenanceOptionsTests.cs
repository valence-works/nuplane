using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Feeds.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;

namespace Nuplane.Runtime.Tests;

public sealed class PackageStoreMaintenanceOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded ")]
    public void Validate_WhenRootLabelIsNullBlankOrPadded_FailsStartup(string? rootLabel)
    {
        var services = CreateServices();
        services.AddNuplane(_ => { });
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = rootLabel);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("PackageStoreMaintenance RootLabel", exception.Message);
    }

    [Fact]
    public void Validate_WhenRootLabelCollidesWithLaterExtraRoot_FailsStartup()
    {
        var services = CreateServices();
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "late-extra");
        services.AddNuplane(builder => builder.AddPackageStoreRoot("early-extra", "early-root"));
        services.AddNuplane(builder => builder.AddPackageStoreRoot("LATE-EXTRA", "late-root"));

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("conflicts with a configured package-store root label", exception.Message);
    }

    [Fact]
    public void Validate_WhenBlankPackageInstallRoot_StillFailsStartup()
    {
        var services = CreateServices();
        services.AddNuplane(_ => { });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = " ");

        using var provider = services.BuildServiceProvider();

        var exception = Record.Exception(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.NotNull(exception);
        IEnumerable<Exception> errors = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        Assert.All(errors, error =>
        {
            var validation = Assert.IsType<OptionsValidationException>(error);
            Assert.Equal(typeof(FeedResolutionOptions), validation.OptionsType);
            Assert.Contains("FeedResolution PackageInstallRoot cannot be blank", validation.Message);
        });
    }

    [Fact]
    public void AddNuplane_WhenConfigurationBindsAndLaterOptionsOverride_UsesFinalRootLabel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PackageStoreMaintenance:RootLabel"] = "configured-alias"
            })
            .Build();
        var configuredServices = CreateServices();
        configuredServices.AddNuplane(configuration);

        using var configuredProvider = configuredServices.BuildServiceProvider();
        configuredProvider.GetRequiredService<IStartupValidator>().Validate();
        Assert.Equal("configured-alias",
            configuredProvider.GetRequiredService<IOptions<PackageStoreMaintenanceOptions>>().Value.RootLabel);

        var overriddenServices = CreateServices();
        overriddenServices.AddNuplane(configuration, builder =>
            builder.Services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "builder-alias"));

        using var overriddenProvider = overriddenServices.BuildServiceProvider();
        overriddenProvider.GetRequiredService<IStartupValidator>().Validate();
        var resolver = overriddenProvider.GetRequiredService<IPackageStoreRootResolver>();

        Assert.True(resolver.TryResolve("builder-alias", out _));
        Assert.False(resolver.TryResolve("configured-alias", out _));
    }

    [Fact]
    public void Resolve_WhenConfiguredRootLabelRenamesDefault_RefusesOldAliasAndReturnsCatalogDescriptor()
    {
        var services = CreateServices();
        var configuredInstallRoot = Path.Combine(Path.GetTempPath(), "maintenance-root-" + Guid.NewGuid().ToString("N"));
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = "early-root");
        services.AddNuplane(builder => builder.AddPackageStoreRoot("extra", "extra-root"));
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "maintenance-default");
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = configuredInstallRoot);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        var catalogRoots = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;
        var resolver = provider.GetRequiredService<IPackageStoreRootResolver>();

        Assert.True(resolver.TryResolve("MAINTENANCE-DEFAULT", out var resolvedDefault));
        var defaultDescriptor = Assert.Single(catalogRoots, static root => root.Label == "default");
        Assert.Same(defaultDescriptor, resolvedDefault);
        Assert.Equal(Path.GetFullPath(configuredInstallRoot), resolvedDefault!.RootPath);
        Assert.False(resolver.TryResolve("default", out var missingDefaultAlias));
        Assert.Null(missingDefaultAlias);

        Assert.True(resolver.TryResolve("EXTRA", out var resolvedExtra));
        var extraDescriptor = Assert.Single(catalogRoots, static root => root.Label == "extra");
        Assert.Same(extraDescriptor, resolvedExtra);
        Assert.Equal("extra", resolvedExtra!.Label);
    }

    [Fact]
    public void Resolve_WhenOnlyConfiguredAliasesAndExtrasAreRequested_RejectsUnknownAndPathLabels()
    {
        var services = CreateServices();
        services.AddNuplane(builder => builder.AddPackageStoreRoot("extra", "extra-root"));
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "maintenance-default");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        var resolver = provider.GetRequiredService<IPackageStoreRootResolver>();

        Assert.False(resolver.TryResolve("unknown", out var unknown));
        Assert.Null(unknown);
        Assert.False(resolver.TryResolve(null, out var missing));
        Assert.Null(missing);
        Assert.False(resolver.TryResolve(Path.Combine(Path.GetTempPath(), "unconfigured-root"), out var path));
        Assert.Null(path);
    }

    [Fact]
    public void Resolve_WhenOptionsChangeAfterSingletonResolution_KeepsFrozenAliasMap()
    {
        var services = CreateServices();
        services.AddNuplane(_ => { });
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "initial-alias");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        var resolver = provider.GetRequiredService<IPackageStoreRootResolver>();
        var options = provider.GetRequiredService<IOptions<PackageStoreMaintenanceOptions>>().Value;
        Assert.True(resolver.TryResolve("initial-alias", out var originalRoot));

        options.RootLabel = "later-alias";
        provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value.PackageInstallRoot = "later-root";

        Assert.True(resolver.TryResolve("initial-alias", out var frozenRoot));
        Assert.Same(originalRoot, frozenRoot);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages"), frozenRoot!.RootPath);
        Assert.False(resolver.TryResolve("later-alias", out _));
        Assert.False(resolver.TryResolve("default", out _));
    }

    [Fact]
    public void AddNuplane_WhenRepeated_RegistersResolverOnceAndIncludesFinalRootDefinition()
    {
        var services = CreateServices();
        services.AddSingleton<IValidateOptions<PackageStoreMaintenanceOptions>, PermissivePackageStoreMaintenanceOptionsValidator>();
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = "later-extra");
        services.AddNuplane(builder => builder.AddPackageStoreRoot("early-extra", "early-root"));
        services.AddNuplane(builder => builder.AddPackageStoreRoot("later-extra", "later-root"));
        services.Configure<PackageStoreMaintenanceOptions>(options => options.RootLabel = " ");

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(PackageStoreRootResolver));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IPackageStoreRootResolver));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(PackageStoreMaintenanceOptionsValidator));
        Assert.Equal(2, services.Count(static descriptor => descriptor.ServiceType == typeof(IValidateOptions<PackageStoreMaintenanceOptions>)));

        using var provider = services.BuildServiceProvider();
        var rootLabels = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots
            .Select(static root => root.Label)
            .ToArray();

        Assert.Equal(["default", "early-extra", "later-extra"], rootLabels);
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("RootLabel must not be null or blank", exception.Message);
        Assert.Single(provider.GetServices<IValidateOptions<PackageStoreMaintenanceOptions>>(),
            static validator => validator is PermissivePackageStoreMaintenanceOptionsValidator);
    }

    [Fact]
    public void AddNuplane_WhenOptionsAreOmitted_UsesDefaultRootWithoutNativeAdmission()
    {
        var services = CreateServices();
        var nativeFileSystemActivated = false;
        services.AddNuplane(_ => { });
        services.Replace(ServiceDescriptor.Singleton<IPhysicalStoreFileSystem>(_ =>
        {
            nativeFileSystemActivated = true;
            throw new InvalidOperationException("Native package-store admission was not expected.");
        }));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        var options = provider.GetRequiredService<IOptions<PackageStoreMaintenanceOptions>>().Value;
        var catalog = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>();
        var resolver = provider.GetRequiredService<IPackageStoreRootResolver>();

        Assert.Equal(["RootLabel"], typeof(PackageStoreMaintenanceOptions).GetProperties().Select(static property => property.Name));
        Assert.Equal("default", options.RootLabel);
        Assert.True(resolver.TryResolve("default", out var resolved));
        var defaultRoot = Assert.Single(catalog.Roots);
        Assert.Same(defaultRoot, resolved);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, ".nuplane", "packages"), defaultRoot.RootPath);
        Assert.False(nativeFileSystemActivated);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private sealed class PermissivePackageStoreMaintenanceOptionsValidator : IValidateOptions<PackageStoreMaintenanceOptions>
    {
        public ValidateOptionsResult Validate(string? name, PackageStoreMaintenanceOptions options) => ValidateOptionsResult.Success;
    }
}
