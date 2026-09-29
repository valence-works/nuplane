using System.Globalization;
using Microsoft.Extensions.Configuration;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

/// <summary>
/// End to end through a composed host: a shared assembly configured the way a host's <c>appsettings.json</c>
/// declares an unsigned one, bound by <c>AutoloadPackages</c>, validated at startup, honoured by host-integrated
/// loading, and still honoured when the host reads the package's assemblies through the assembly catalog.
/// </summary>
public sealed class AutoloadPackagesSharedAssemblyTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-shared-assembly-e2e-");

    public void Dispose()
    {
        try
        {
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
        await using var host = new NuplaneHostFixture(loadingConfiguration: loadingConfiguration);
        var package = HostFreeLoadTestSupport.ActivePackage(
            SharedAssemblyTestSupport.ConsumerPackageId,
            SharedAssemblyTestSupport.CreateConsumerPackageInstall(_tempDir));

        await host.ActivateAsync(package);
        var assemblies = await host.ReadAssembliesAsync();

        var loaded = Assert.Single((await host.ReadLoadStateAsync()).Packages);
        Assert.Equal(PackageLoadStatus.Loaded, loaded.Status);
        Assert.Equal(PackageLoadMode.HostIntegrated, loaded.LoadMode);
        Assert.All(
            assemblies.Where(static assembly => assembly.GetName().Name == SharedAssemblyTestSupport.SharedAssemblyName),
            static assembly => Assert.Same(SharedAssemblyTestSupport.HostSharedAssembly, assembly));
        var consumer = Assert.Single(assemblies, static assembly => assembly.GetName().Name == SharedAssemblyTestSupport.ConsumerPackageId);
        Assert.Same(typeof(HealthyFixtureType), SharedAssemblyTestSupport.BoundSharedType(consumer));
    }
}
