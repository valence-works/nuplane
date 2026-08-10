using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Setup;

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

    internal static void ApplySetupConfiguration(
        NuplaneBuilder builder,
        IConfiguration setupConfiguration,
        IConfiguration reconciliationConfiguration)
    {
        ApplyAutomaticReconciliation(builder, setupConfiguration, reconciliationConfiguration);

        var stateFilePath = setupConfiguration[nameof(NuplaneSetupOptions.StateFilePath)];
        if (!string.IsNullOrWhiteSpace(stateFilePath))
        {
            builder.WithStateFile(stateFilePath);
        }

        if (setupConfiguration.GetValue<bool?>(nameof(NuplaneSetupOptions.UseInMemoryStore)) is true)
        {
            builder.UseInMemoryStore();
        }

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
}
