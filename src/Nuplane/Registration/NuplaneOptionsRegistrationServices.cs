using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Options.Validation;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Setup;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class NuplaneOptionsRegistrationServices
{
    internal const string SetupSectionName = "Setup";
    private const string ReconciliationSectionName = "Reconciliation";
    private const string FeedResolutionSectionName = "FeedResolution";
    private const string SourceTrustSectionName = "SourceTrust";
    private const string FeedTrustPolicySectionName = "FeedTrustPolicy";
    private const string LockFileSectionName = "LockFile";
    private const string CleanupPolicySectionName = "CleanupPolicy";
    private const string ConvergenceSectionName = "Convergence";
    private const string TrustedSourcePolicySectionName = "TrustedSourcePolicy";
    private const string StoreRegistrySectionName = "StoreRegistry";

    private static readonly Action<IServiceCollection, IConfiguration>[] ConfiguredOptionBinders =
    [
        static (services, configuration) => ConfigureBoundOptions<NuplaneSetupOptions>(services, configuration, SetupSectionName),
        static (services, configuration) => ConfigureBoundOptions<ReconciliationOptions>(services, configuration, ReconciliationSectionName),
        static (services, configuration) => ConfigureBoundOptions<FeedResolutionOptions>(services, configuration, FeedResolutionSectionName),
        static (services, configuration) => ConfigureBoundOptions<SourceTrustOptions>(services, configuration, SourceTrustSectionName),
        static (services, configuration) => ConfigureBoundOptions<FeedTrustPolicyOptions>(services, configuration, FeedTrustPolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<LockFileOptions>(services, configuration, LockFileSectionName),
        static (services, configuration) => ConfigureBoundOptions<CleanupPolicyOptions>(services, configuration, CleanupPolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<ConvergenceOptions>(services, configuration, ConvergenceSectionName),
        static (services, configuration) => ConfigureBoundOptions<TrustedSourcePolicyOptions>(services, configuration, TrustedSourcePolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<StoreRegistryOptions>(services, configuration, StoreRegistrySectionName)
    ];

    internal static void RegisterValidators(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<NuplaneSetupOptions>, NuplaneSetupOptionsValidator>();
        services.AddSingleton<IValidateOptions<ReconciliationOptions>, ReconciliationOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedResolutionOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedTrustPolicyOptions>, FeedTrustPolicyOptionsValidator>();
        services.AddSingleton<IValidateOptions<LockFileOptions>, LockFileOptionsValidator>();
        services.AddSingleton<IValidateOptions<CleanupPolicyOptions>, CleanupPolicyOptionsValidator>();
        services.AddSingleton<FeedCredentialOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedCredentialCompositeValidator>();
        services.AddSingleton<IValidateOptions<ConvergenceOptions>, ConvergenceOptionsValidator>();
        services.AddSingleton<IValidateOptions<TrustedSourcePolicyOptions>, TrustedSourcePolicyOptionsValidator>();
        services.AddSingleton<IValidateOptions<StoreRegistryOptions>, StoreRegistryOptionsValidator>();
    }

    internal static void RegisterOptions(IServiceCollection services)
    {
        services.AddOptions<NuplaneSetupOptions>().ValidateOnStart();
        services.AddOptions<SourceTrustOptions>().ValidateOnStart();
        services.AddOptions<ReconciliationOptions>().ValidateOnStart();
        services.AddOptions<FeedResolutionOptions>().ValidateOnStart();
        services.AddOptions<FeedTrustPolicyOptions>().ValidateOnStart();
        services.AddOptions<LockFileOptions>().ValidateOnStart();
        services.AddOptions<CleanupPolicyOptions>().ValidateOnStart();
        services.AddOptions<ConvergenceOptions>().ValidateOnStart();
        services.AddOptions<TrustedSourcePolicyOptions>().ValidateOnStart();
        services.AddOptions<StoreRegistryOptions>().ValidateOnStart();
    }

    internal static void BindConfiguredOptions(IServiceCollection services, IConfiguration configuration)
    {
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
