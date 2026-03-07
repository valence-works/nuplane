using Microsoft.Extensions.DependencyInjection;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class CoreRuntimeRegistrationIsolationTests
{
    [Fact]
    public async Task AddNuplane_ResolvesAndRunsWithoutLoadingRegistration()
    {
        var services = new ServiceCollection();
        var stateRoot = Path.Combine(Path.GetTempPath(), "nuplane-core-registration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var stateFilePath = Path.Combine(stateRoot, "state.json");

        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(stateFilePath);
            // No AutoloadPackages() — verifies the runtime resolves without loading registration.
        });

        await using var provider = services.BuildServiceProvider();

        var runtime = provider.GetRequiredService<ReconciliationService>();
        var result = await runtime.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.FailedPackages);
    }
}
