using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Feeds.Configuration;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Validation;
using Nuplane.Setup;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;
using Nuplane.Store.Validation;

namespace Nuplane.Registration;

internal static class NuplaneOptionsRegistrationServices
{
    internal const string SetupSectionName = "Setup";
    private const string ReconciliationSectionName = "Reconciliation";
    private const string FeedResolutionSectionName = "FeedResolution";
    private const string LockFileSectionName = "LockFile";
    private const string CleanupPolicySectionName = "CleanupPolicy";
    private const string ConvergenceSectionName = "Convergence";
    private const string StoreRegistrySectionName = "StoreRegistry";

    private static readonly Action<IServiceCollection, IConfiguration>[] ConfiguredOptionBinders =
    [
        static (services, configuration) => ConfigureBoundOptions<NuplaneSetupOptions>(services, configuration, SetupSectionName),
        static (services, configuration) => ConfigureBoundOptions<ReconciliationOptions>(services, configuration, ReconciliationSectionName),
        static (services, configuration) => ConfigureBoundOptions<FeedResolutionOptions>(services, configuration, FeedResolutionSectionName),
        static (services, configuration) => ConfigureBoundOptions<LockFileOptions>(services, configuration, LockFileSectionName),
        static (services, configuration) => ConfigureBoundOptions<CleanupPolicyOptions>(services, configuration, CleanupPolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<ConvergenceOptions>(services, configuration, ConvergenceSectionName),
        static (services, configuration) => ConfigureBoundOptions<StoreRegistryOptions>(services, configuration, StoreRegistrySectionName)
    ];

    internal static void RegisterValidators(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<NuplaneSetupOptions>, NuplaneSetupOptionsValidator>();
        services.AddSingleton<IValidateOptions<ReconciliationOptions>, ReconciliationOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedResolutionOptionsValidator>();
        services.AddSingleton<IValidateOptions<LockFileOptions>, LockFileOptionsValidator>();
        services.AddSingleton<IValidateOptions<CleanupPolicyOptions>, CleanupPolicyOptionsValidator>();
        services.AddSingleton<FeedCredentialOptionsValidator>();
        services.AddSingleton<IValidateOptions<ConvergenceOptions>, ConvergenceOptionsValidator>();
        services.AddSingleton<IValidateOptions<StoreRegistryOptions>, StoreRegistryOptionsValidator>();
    }

    internal static void RegisterOptions(this IServiceCollection services)
    {
        services.AddOptions<NuplaneSetupOptions>().ValidateOnStart();
        services.AddOptions<ReconciliationOptions>().ValidateOnStart();
        services.AddOptions<FeedResolutionOptions>().ValidateOnStart();
        services.AddOptions<LockFileOptions>().ValidateOnStart();
        services.AddOptions<CleanupPolicyOptions>().ValidateOnStart();
        services.AddOptions<ConvergenceOptions>().ValidateOnStart();
        services.AddOptions<StoreRegistryOptions>().ValidateOnStart();
    }

    internal static void BindConfiguredOptions(IServiceCollection services, IConfiguration configuration)
    {
        var setupSection = GetNamedSectionOrSelf(configuration, SetupSectionName);
        services.Replace(ServiceDescriptor.Singleton<INuplaneSetupFeedDeclarationSource>(
            new ConfigurationNuplaneSetupFeedDeclarationSource(setupSection)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<NuplaneSetupOptions>, NuplaneSetupFeedDiagnosticReporter>());

        foreach (var bindOptions in ConfiguredOptionBinders)
        {
            bindOptions(services, configuration);
        }
    }

    internal static IConfigurationSection GetNamedSectionOrSelf(IConfiguration configuration, string sectionName)
    {
        if (configuration is IConfigurationSection section
            && string.Equals(section.Key, sectionName, StringComparison.OrdinalIgnoreCase))
        {
            return section;
        }

        return configuration.GetSection(sectionName);
    }

    private static void ConfigureBoundOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class, new()
    {
        var section = GetNamedSectionOrSelf(configuration, sectionName);
        services.Configure<TOptions>(options => section.Bind(options));
    }
}
