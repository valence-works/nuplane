using Microsoft.Extensions.Configuration;

namespace Nuplane.Loading.Tests;

public sealed class AutoloadPackagesConfigurationTests
{
    [Theory]
    [InlineData(null, PackageLoadStateAvailability.Available, PackageLoadStatus.Loaded)]
    [InlineData("false", PackageLoadStateAvailability.Disabled, PackageLoadStatus.Disabled)]
    public async Task AutoloadPackages_EnabledConfiguration_ControlsLoading(
        string? configuredEnabled,
        PackageLoadStateAvailability expectedAvailability,
        PackageLoadStatus expectedStatus)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredEnabled is not null)
            settings["Nuplane:Loading:Enabled"] = configuredEnabled;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var directory = Directory.CreateTempSubdirectory("nuplane-autoload-config-");

        try
        {
            await using var host = new NuplaneHostFixture(loadingConfiguration: configuration.GetSection("Nuplane:Loading"));
            var package = HostFreeLoadTestSupport.EmitPackage(directory, "AutoloadConfiguration");

            await host.ActivateAsync(package.AsActivePackage());
            var snapshot = await host.ReadLoadStateAsync();

            Assert.Equal(expectedAvailability, snapshot.Availability);
            var loadState = Assert.Single(snapshot.Packages);
            Assert.Equal(package.PackageId, loadState.PackageId);
            Assert.Equal(expectedStatus, loadState.Status);
        }
        finally
        {
            try
            {
                // Host-integrated assemblies can remain locked until process exit on Windows.
                directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
