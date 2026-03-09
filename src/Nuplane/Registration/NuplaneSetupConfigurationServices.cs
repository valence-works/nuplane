using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
using Nuplane.Setup;

namespace Nuplane.Registration;

internal static class NuplaneSetupConfigurationServices
{
    internal static IConfigurationSection GetSetupSectionOrSelf(IConfiguration configuration) =>
        NuplaneOptionsRegistrationServices.GetNamedSectionOrSelf(
            configuration,
            NuplaneOptionsRegistrationServices.SetupSectionName);

    internal static void ApplySetupConfiguration(NuplaneBuilder builder, IConfiguration configuration)
    {
        if (configuration.GetValue<bool?>(nameof(NuplaneSetupOptions.AutomaticReconciliation)) is true)
        {
            builder.PollEvery(
                configuration.GetValue<TimeSpan?>(nameof(NuplaneSetupOptions.PollInterval))
                ?? TimeSpan.FromSeconds(60));
        }

        var stateFilePath = configuration[nameof(NuplaneSetupOptions.StateFilePath)];
        if (!string.IsNullOrWhiteSpace(stateFilePath))
        {
            builder.WithStateFile(stateFilePath);
        }

        if (configuration.GetValue<bool?>(nameof(NuplaneSetupOptions.UseInMemoryStore)) is true)
        {
            builder.UseInMemoryStore();
        }

        NuplaneFeedSetupConfiguration.ApplyConfiguredFeeds(builder, configuration);
    }
}

