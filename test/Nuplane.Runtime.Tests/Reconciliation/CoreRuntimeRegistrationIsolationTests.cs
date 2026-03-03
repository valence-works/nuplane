using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class CoreRuntimeRegistrationIsolationTests
{
    [Fact]
    public async Task AddNuplaneRuntime_ResolvesAndRunsWithoutLoadingRegistration()
    {
        var services = new ServiceCollection();
        var stateRoot = Path.Combine(Path.GetTempPath(), "nuplane-core-registration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var stateFilePath = Path.Combine(stateRoot, "state.json");

        services.AddNuplaneRuntime(
            configureSourceTrust: trust =>
            {
                trust.AllowedSourceNames.Add("NuGet.Main");
                trust.AllowedPackageIds.Add("Test.Package");
            },
            configureFeeds: feeds =>
            {
                feeds.Add(new FeedDefinition(
                    Name: "NuGet.Main",
                    ServiceIndex: new Uri("https://api.nuget.org/v3/index.json"),
                    TrustLevel: FeedTrustLevel.Trusted,
                    Credentials: "secrets://nuget/main"));
            },
            stateFilePath: stateFilePath);

        using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<ReconciliationService>();
        var result = await runtime.TriggerManualAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.FailedPackages);
    }
}
