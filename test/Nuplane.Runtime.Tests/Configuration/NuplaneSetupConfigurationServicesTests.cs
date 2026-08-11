using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Hosting;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class NuplaneSetupConfigurationServicesTests
{
    // Truth table from issue #54: the explicitly present Reconciliation key decides in both directions,
    // and the Setup shorthand only applies when the Reconciliation key is absent.
    [Theory]
    [InlineData("true", null, true)]
    [InlineData("true", "false", false)]
    [InlineData("false", "true", true)]
    [InlineData("false", null, false)]
    [InlineData(null, null, false)]
    public void ApplySetupConfiguration_SetupAndReconciliationEnableCombination_PrefersReconciliationSection(
        string? setupValue,
        string? reconciliationValue,
        bool expected)
    {
        // Arrange
        var settings = new Dictionary<string, string?>();
        if (setupValue is not null)
        {
            settings["Nuplane:Setup:AutomaticReconciliation"] = setupValue;
        }

        if (reconciliationValue is not null)
        {
            settings["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = reconciliationValue;
        }

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.Equal(expected, options.EnableAutomaticReconciliation);
    }

    [Fact]
    public void ApplySetupConfiguration_UnrelatedReconciliationKeysOnly_LeavesAutomaticReconciliationDisabled()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Reconciliation:MaxRetryAttempts"] = "5"
        };

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.False(options.EnableAutomaticReconciliation);
        Assert.Equal(5, options.MaxRetryAttempts);
    }

    [Fact]
    public void ApplySetupConfiguration_SetupEnabledAndReconciliationExplicitlyDisabled_DoesNotRegisterSchedulerHostedService()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = "false"
        };

        // Act
        var services = AddNuplaneFromConfiguration(settings);

        // Assert
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(ReconciliationHostedService));
    }

    [Fact]
    public void ApplySetupConfiguration_ReconciliationExplicitlyEnabledWithoutSetupShorthand_RegistersSchedulerHostedService()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = "true"
        };

        // Act
        var services = AddNuplaneFromConfiguration(settings);

        // Assert
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(ReconciliationHostedService));
    }

    [Fact]
    public void ApplySetupConfiguration_SetupPollIntervalWithoutReconciliationPollInterval_UsesSetupPollInterval()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Setup:PollInterval"] = "00:00:45"
        };

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromSeconds(45), options.PollInterval);
    }

    [Fact]
    public void ApplySetupConfiguration_ReconciliationPollIntervalWithSetupShorthand_UsesReconciliationPollInterval()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Setup:PollInterval"] = "00:00:45",
            ["Nuplane:Reconciliation:PollInterval"] = "00:05:00"
        };

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromMinutes(5), options.PollInterval);
    }

    [Fact]
    public void ApplySetupConfiguration_SetupEnabledWithoutAnyPollInterval_UsesDefaultPollInterval()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true"
        };

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), options.PollInterval);
    }

    [Fact]
    public void ApplySetupConfiguration_SetupEnabledWithReconciliationStartupFailurePolicy_PreservesStartupFailurePolicy()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Reconciliation:StartupFailurePolicy"] = nameof(StartupFailurePolicy.UseLastKnownGood)
        };

        // Act
        var options = ResolveReconciliationOptions(settings);

        // Assert
        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(StartupFailurePolicy.UseLastKnownGood, options.StartupFailurePolicy);
    }

    [Fact]
    public void ApplySetupConfiguration_SetupSectionPassedDirectly_EnablesAutomaticReconciliation()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Setup:PollInterval"] = "00:00:30"
        });
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNuplane(configuration.GetSection("Nuplane:Setup"));

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PollInterval);
    }

    [Fact]
    public void ApplySetupConfiguration_BuilderPollEveryAfterExplicitDisable_EnablesAutomaticReconciliation()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["Nuplane:Setup:AutomaticReconciliation"] = "true",
            ["Nuplane:Reconciliation:EnableAutomaticReconciliation"] = "false"
        };
        var configuration = BuildConfiguration(settings);
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNuplane(
            configuration.GetSection("Nuplane"),
            nuplane => nuplane.PollEvery(TimeSpan.FromSeconds(10)));

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
        Assert.True(options.EnableAutomaticReconciliation);
        Assert.Equal(TimeSpan.FromSeconds(10), options.PollInterval);
    }

    private static IConfigurationRoot BuildConfiguration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IServiceCollection AddNuplaneFromConfiguration(Dictionary<string, string?> settings)
    {
        var configuration = BuildConfiguration(settings);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(configuration.GetSection("Nuplane"));
        return services;
    }

    private static ReconciliationOptions ResolveReconciliationOptions(Dictionary<string, string?> settings)
    {
        var services = AddNuplaneFromConfiguration(settings);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
    }
}
