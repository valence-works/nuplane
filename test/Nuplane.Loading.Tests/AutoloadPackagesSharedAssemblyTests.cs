using System.Globalization;
using Microsoft.Extensions.Configuration;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

/// <summary>
/// End to end through a composed host: a shared assembly configured the way a host's <c>appsettings.json</c>
/// declares an unsigned one, bound by <c>AutoloadPackages</c>, validated at startup, honoured by host-integrated
/// loading, and still honoured when the host reads the package's assemblies through the assembly catalog.
/// </summary>
public sealed class AutoloadPackagesSharedAssemblyTests : IAsyncDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-shared-assembly-e2e-");
    private readonly NuplaneHostFixture _host;

    public AutoloadPackagesSharedAssemblyTests()
    {
        var loadingConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nuplane:Loading:Enabled"] = "true",
                ["Nuplane:Loading:SharedAssemblies:0:Name"] = SharedAssemblyTestSupport.SharedAssemblyName,
                // What { "PublicKeyToken": null } in a host's appsettings.json becomes: the key, with a null value.
                ["Nuplane:Loading:SharedAssemblies:0:PublicKeyToken"] = null,
                ["Nuplane:Loading:SharedAssemblies:0:MajorVersion"] = SharedAssemblyTestSupport.SharedAssemblyMajorVersion.ToString(CultureInfo.InvariantCulture)
            })
            .Build()
            .GetSection("Nuplane:Loading");
        _host = new NuplaneHostFixture(loadingConfiguration: loadingConfiguration);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try
        {
            // Files loaded into non-collectible contexts can stay locked for the process lifetime.
            _tempDir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AutoloadPackages_UnsignedSharedAssemblyFromConfiguration_HostIntegratedPackageCarryingItsOwnCopyBindsHostCopy()
    {
        Assert.Empty(SharedAssemblyTestSupport.HostSharedAssembly.GetName().GetPublicKeyToken() ?? []);
        var package = HostFreeLoadTestSupport.ActivePackage(
            SharedAssemblyTestSupport.ConsumerPackageId,
            SharedAssemblyTestSupport.CreateConsumerPackageInstall(_tempDir));

        await _host.ActivateAsync(package);
        var assemblies = await _host.ReadAssembliesAsync();

        var loaded = Assert.Single((await _host.ReadLoadStateAsync()).Packages);
        Assert.Equal(PackageLoadStatus.Loaded, loaded.Status);
        Assert.Equal(PackageLoadMode.HostIntegrated, loaded.LoadMode);
        Assert.Empty(SharedAssemblyTestSupport.SharedAssembliesIn(assemblies));
        var consumer = Assert.Single(assemblies, static assembly => assembly.GetName().Name == SharedAssemblyTestSupport.ConsumerPackageId);
        Assert.Same(typeof(HealthyFixtureType), SharedAssemblyTestSupport.BoundSharedType(consumer));
    }
}
