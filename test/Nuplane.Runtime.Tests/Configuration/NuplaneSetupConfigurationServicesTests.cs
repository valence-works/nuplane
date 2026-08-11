using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Hosting;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Store.State;

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

    // The store persistence layer follows the same precedence rule as the Reconciliation section:
    // an explicitly present StoreRegistry key decides in both directions, and the Setup shorthand
    // only applies when the matching StoreRegistry key is absent.
    [Theory]
    [InlineData("./setup-state.json", null, "./setup-state.json")]
    [InlineData(null, "./store-state.json", "./store-state.json")]
    [InlineData("./setup-state.json", "./store-state.json", "./store-state.json")]
    public void ApplySetupConfiguration_SetupAndStoreRegistryStateFilePathCombination_PrefersStoreRegistrySection(
        string? setupValue,
        string? storeRegistryValue,
        string expectedPath)
    {
        // Arrange
        var settings = BuildStoreSettings(("Setup:StateFilePath", setupValue), ("StoreRegistry:StateFilePath", storeRegistryValue));

        // Act
        var settingsResult = ResolveStorePersistenceSettings(settings);

        // Assert
        Assert.Equal(StorePersistenceMode.ConfiguredPath, settingsResult.Mode);
        Assert.Equal(Path.GetFullPath(expectedPath), settingsResult.ResolvedStateFilePath);
    }

    [Theory]
    [InlineData("true", null, StorePersistenceMode.InMemory)]
    [InlineData("true", "false", StorePersistenceMode.DefaultPath)]
    [InlineData("false", "true", StorePersistenceMode.InMemory)]
    [InlineData(null, "true", StorePersistenceMode.InMemory)]
    [InlineData("false", null, StorePersistenceMode.DefaultPath)]
    [InlineData(null, null, StorePersistenceMode.DefaultPath)]
    public void ApplySetupConfiguration_SetupAndStoreRegistryUseInMemoryStoreCombination_PrefersStoreRegistrySection(
        string? setupValue,
        string? storeRegistryValue,
        StorePersistenceMode expectedMode)
    {
        // Arrange
        var settings = BuildStoreSettings(("Setup:UseInMemoryStore", setupValue), ("StoreRegistry:UseInMemoryStore", storeRegistryValue));

        // Act
        var settingsResult = ResolveStorePersistenceSettings(settings);

        // Assert
        Assert.Equal(expectedMode, settingsResult.Mode);
    }

    [Fact]
    public void ApplySetupConfiguration_StoreRegistryStateFilePathWithSetupInMemoryShorthand_UsesStoreRegistryPath()
    {
        // Arrange
        var settings = BuildStoreSettings(("Setup:UseInMemoryStore", "true"), ("StoreRegistry:StateFilePath", "./store-state.json"));

        // Act
        var settingsResult = ResolveStorePersistenceSettings(settings);

        // Assert
        Assert.Equal(StorePersistenceMode.ConfiguredPath, settingsResult.Mode);
        Assert.Equal(Path.GetFullPath("./store-state.json"), settingsResult.ResolvedStateFilePath);
    }

    [Fact]
    public void ApplySetupConfiguration_StoreRegistryInMemoryStoreWithSetupStateFilePathShorthand_UsesInMemoryStore()
    {
        // Arrange
        var settings = BuildStoreSettings(("Setup:StateFilePath", "./setup-state.json"), ("StoreRegistry:UseInMemoryStore", "true"));

        // Act
        var settingsResult = ResolveStorePersistenceSettings(settings);

        // Assert
        Assert.Equal(StorePersistenceMode.InMemory, settingsResult.Mode);
        Assert.Null(settingsResult.ResolvedStateFilePath);
    }

    [Fact]
    public void ApplySetupConfiguration_StoreRegistryInMemoryStoreDisabledWithSetupStateFilePathShorthand_UsesSetupPath()
    {
        // Arrange
        var settings = BuildStoreSettings(("Setup:StateFilePath", "./setup-state.json"), ("StoreRegistry:UseInMemoryStore", "false"));

        // Act
        var settingsResult = ResolveStorePersistenceSettings(settings);

        // Assert
        Assert.Equal(StorePersistenceMode.ConfiguredPath, settingsResult.Mode);
        Assert.Equal(Path.GetFullPath("./setup-state.json"), settingsResult.ResolvedStateFilePath);
    }

    [Fact]
    public void ApplySetupConfiguration_BuilderWithStateFileAfterStoreRegistrySection_UsesBuilderPath()
    {
        // Arrange
        var configuration = BuildConfiguration(BuildStoreSettings(
            ("Setup:StateFilePath", "./setup-state.json"),
            ("StoreRegistry:StateFilePath", "./store-state.json")));
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNuplane(
            configuration.GetSection("Nuplane"),
            nuplane => nuplane.WithStateFile("./builder-state.json"));

        // Assert
        using var provider = services.BuildServiceProvider();
        var settingsResult = provider.GetRequiredService<EffectiveStorePersistenceSettings>();
        Assert.Equal(StorePersistenceMode.ConfiguredPath, settingsResult.Mode);
        Assert.Equal(Path.GetFullPath("./builder-state.json"), settingsResult.ResolvedStateFilePath);
    }

    [Fact]
    public void ApplySetupConfiguration_SetupSectionPassedDirectly_UsesSetupStateFilePath()
    {
        // Arrange
        var configuration = BuildConfiguration(BuildStoreSettings(("Setup:StateFilePath", "./setup-state.json")));
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddNuplane(configuration.GetSection("Nuplane:Setup"));

        // Assert
        using var provider = services.BuildServiceProvider();
        var settingsResult = provider.GetRequiredService<EffectiveStorePersistenceSettings>();
        Assert.Equal(StorePersistenceMode.ConfiguredPath, settingsResult.Mode);
        Assert.Equal(Path.GetFullPath("./setup-state.json"), settingsResult.ResolvedStateFilePath);
    }

    private static Dictionary<string, string?> BuildStoreSettings(params (string Key, string? Value)[] entries)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (key, value) in entries.Where(static entry => entry.Value is not null))
        {
            settings[$"Nuplane:{key}"] = value;
        }

        return settings;
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

    private static EffectiveStorePersistenceSettings ResolveStorePersistenceSettings(Dictionary<string, string?> settings)
    {
        var services = AddNuplaneFromConfiguration(settings);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<EffectiveStorePersistenceSettings>();
    }
}
