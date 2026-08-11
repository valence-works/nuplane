using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Setup;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class NuplaneSetupConfigurationServices
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

    internal static IConfigurationSection GetSetupSectionOrSelf(IConfiguration configuration) =>
        NuplaneOptionsRegistrationServices.GetNamedSectionOrSelf(
            configuration,
            NuplaneOptionsRegistrationServices.SetupSectionName);

    internal static IConfigurationSection GetReconciliationSectionOrSelf(IConfiguration configuration) =>
        NuplaneOptionsRegistrationServices.GetNamedSectionOrSelf(
            configuration,
            NuplaneOptionsRegistrationServices.ReconciliationSectionName);

    internal static IConfigurationSection GetStoreRegistrySectionOrSelf(IConfiguration configuration) =>
        NuplaneOptionsRegistrationServices.GetNamedSectionOrSelf(
            configuration,
            NuplaneOptionsRegistrationServices.StoreRegistrySectionName);

    internal static void ApplySetupConfiguration(
        NuplaneBuilder builder,
        IConfiguration setupConfiguration,
        IConfiguration reconciliationConfiguration,
        IConfiguration storeRegistryConfiguration)
    {
        ApplyAutomaticReconciliation(builder, setupConfiguration, reconciliationConfiguration);
        ApplyStorePersistence(builder, setupConfiguration, storeRegistryConfiguration);

        NuplaneFeedSetupConfiguration.ApplyConfiguredFeeds(builder, setupConfiguration);
    }

    // The dedicated Reconciliation section is more specific than the Setup shorthand, so an explicitly
    // present key there wins in both directions. Values are read as nullable so that an explicit false is
    // distinguishable from an absent key, which a bound option default cannot express.
    private static void ApplyAutomaticReconciliation(
        NuplaneBuilder builder,
        IConfiguration setupConfiguration,
        IConfiguration reconciliationConfiguration)
    {
        var enabled =
            reconciliationConfiguration.GetValue<bool?>(nameof(ReconciliationOptions.EnableAutomaticReconciliation))
            ?? setupConfiguration.GetValue<bool?>(nameof(NuplaneSetupOptions.AutomaticReconciliation));

        if (enabled is not true)
        {
            return;
        }

        var pollInterval =
            reconciliationConfiguration.GetValue<TimeSpan?>(nameof(ReconciliationOptions.PollInterval))
            ?? setupConfiguration.GetValue<TimeSpan?>(nameof(NuplaneSetupOptions.PollInterval))
            ?? DefaultPollInterval;

        builder.PollEvery(pollInterval);
    }

    // The dedicated StoreRegistry section is more specific than the Setup shorthand, so an explicitly
    // present key there wins in both directions: section binding already applied it, and the matching
    // Setup shorthand is skipped. An explicit persistence choice in that section also suppresses the
    // opposing shorthand, so the two layers can never combine into a state the validator rejects.
    private static void ApplyStorePersistence(
        NuplaneBuilder builder,
        IConfiguration setupConfiguration,
        IConfiguration storeRegistryConfiguration)
    {
        var storeStateFilePath = storeRegistryConfiguration.GetValue<string>(nameof(StoreRegistryOptions.StateFilePath));
        var storeUseInMemoryStore = storeRegistryConfiguration.GetValue<bool?>(nameof(StoreRegistryOptions.UseInMemoryStore));
        var storeSelectsPath = !string.IsNullOrWhiteSpace(storeStateFilePath);
        var storeSelectsInMemory = storeUseInMemoryStore is true;

        var stateFilePath = setupConfiguration[nameof(NuplaneSetupOptions.StateFilePath)];
        if (storeStateFilePath is null && !storeSelectsInMemory && !string.IsNullOrWhiteSpace(stateFilePath))
        {
            builder.WithStateFile(stateFilePath);
        }

        if (storeUseInMemoryStore is null
            && !storeSelectsPath
            && setupConfiguration.GetValue<bool?>(nameof(NuplaneSetupOptions.UseInMemoryStore)) is true)
        {
            builder.UseInMemoryStore();
        }
    }
}
