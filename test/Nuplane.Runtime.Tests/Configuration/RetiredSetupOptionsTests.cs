using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Hosting;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class RetiredSetupOptionsTests
{
    [Theory]
    [InlineData("AutomaticReconciliation", "false", "Reconciliation:EnableAutomaticReconciliation")]
    [InlineData("PollInterval", "00:00:20", "Reconciliation:PollInterval")]
    [InlineData("StateFilePath", null, "StoreRegistry:StateFilePath")]
    [InlineData("UseInMemoryStore", "true", "StoreRegistry:UseInMemoryStore")]
    public void AddNuplane_RetiredSetupKey_FailsStartupWithReplacement(
        string oldKey, string? oldValue, string replacementKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Nuplane:Setup:{oldKey}"] = oldValue,
            [$"Nuplane:{replacementKey}"] = oldValue
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"));

        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains($"Nuplane:Setup:{oldKey}", error.Message);
        Assert.Contains($"Nuplane:{replacementKey}", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddNuplane_AutomaticReconciliation_RegistersSchedulerWhenEnabled(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reconciliation:EnableAutomaticReconciliation"] = enabled.ToString(),
            ["Reconciliation:PollInterval"] = "00:00:45"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
        Assert.Equal(enabled, options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromSeconds(45), options.PollInterval);
        var scheduler = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ReconciliationHostedService));
        if (enabled)
            Assert.Single(scheduler);
        else
            Assert.Empty(scheduler);
    }
}
