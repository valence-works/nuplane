using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Builder;

namespace Nuplane.Runtime.Tests;

public sealed class PackageArchiveReaderRegistrationTests
{
    [Fact]
    public void AddNuplane_Configuration_BindsArchiveReaderOptionsAndRegistersOneSingletonReader()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nuplane:PackageArchives:CacheBytes"] = "0",
            ["Nuplane:PackageArchives:MaximumArchiveBytes"] = "1048576"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"), _ => { });
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IPackageArchiveReader));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<PackageArchiveReaderOptions>>().Value;
        var reader = provider.GetRequiredService<IPackageArchiveReader>();

        Assert.Equal(0, options.CacheBytes);
        Assert.Equal(1048576, options.MaximumArchiveBytes);
        Assert.IsType<PackageArchiveReader>(reader);
        Assert.Same(reader, provider.GetRequiredService<IPackageArchiveReader>());
    }

    [Fact]
    public void AddNuplane_InvalidArchiveReaderOptions_FailValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nuplane:PackageArchives:CacheBytes"] = "-1"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"), _ => { });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<PackageArchiveReaderOptions>>().Value);
    }

    [Fact]
    public void AddNuplane_CoordinatedComposition_RegistersTheArchiveReader()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nuplane-archive-registration-{Guid.NewGuid():N}");
        var statePath = Path.Combine(root, "state", "first.json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder =>
        {
            builder.WithStateFile(statePath);
            builder.UseCoordinatedPackageStore(
                [new PackageStoreMemberDeclaration("default", "first", statePath)], allClientsParticipate: true);
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<PackageArchiveReader>(provider.GetRequiredService<IPackageArchiveReader>());
        Assert.False(Directory.Exists(root));
    }
}
