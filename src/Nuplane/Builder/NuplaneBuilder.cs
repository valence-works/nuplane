using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

namespace Nuplane.Builder;

/// <summary>
/// Fluent builder for configuring the Nuplane runtime. Obtain an instance via
/// <see cref="NuplaneServiceCollectionExtensions.AddNuplane(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{NuplaneBuilder})"/>.
/// </summary>
public sealed class NuplaneBuilder
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/>.</summary>
    public IServiceCollection Services { get; }

    internal NuplaneBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Enables automatic background reconciliation and sets the polling interval.
    /// </summary>
    /// <param name="interval">How often the reconciliation cycle runs.</param>
    public NuplaneBuilder PollEvery(TimeSpan interval)
    {
        Services.Configure<ReconciliationOptions>(options =>
        {
            options.EnableAutomaticReconciliation = true;
            options.PollInterval = interval;
        });

        NuplaneServiceCollectionExtensions.EnsureTriggerIngressServices(Services);
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReconciliationHostedService>());
        return this;
    }

    /// <summary>
    /// Registers a named feed as a desired-state source. Call <see cref="NuplaneFeedBuilder.FromDirectory"/>
    /// or <see cref="NuplaneFeedBuilder.FromUri"/> inside <paramref name="configure"/> to set the feed location.
    /// </summary>
    /// <param name="name">The unique name of the feed.</param>
    /// <param name="configure">A callback to configure the feed's location, trust, and package patterns.</param>
    public NuplaneBuilder AddFeed(string name, Action<NuplaneFeedBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (HasRegisteredFeed(name))
        {
            throw new InvalidOperationException($"A Nuplane feed named '{name}' has already been registered.");
        }

        var feedBuilder = new NuplaneFeedBuilder(name);
        configure(feedBuilder);

        NuplaneServiceCollectionExtensions.RegisterBuilderFeed(Services, feedBuilder);
        Services.AddSingleton(new NuplaneFeedRegistration(
            feedBuilder.Name,
            DistinctNonBlank(feedBuilder.IncludePatterns).ToArray(),
            HasExplicitUnrestrictedPackageSelection(feedBuilder)));
        return this;
    }

    /// <summary>
    /// Specifies the file path for persisting store state across host restarts.
    /// When not set, state is kept in memory only.
    /// </summary>
    /// <param name="path">The file path for the state file.</param>
    public NuplaneBuilder WithStateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Services.Configure<StoreRegistryOptions>(options =>
        {
            options.StateFilePath = path;
        });
        return this;
    }

    /// <summary>
    /// Registers a reconciliation event observer that is notified when packages change.
    /// The type <typeparamref name="T"/> is resolved from DI and must be registered
    /// as a transient, scoped, or singleton service.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="INuplaneObserver"/>.</typeparam>
    public NuplaneBuilder OnPackagesChanged<T>() where T : class, INuplaneObserver
    {
        Services.AddSingleton<INuplaneObserver, T>();
        return this;
    }

    private bool HasRegisteredFeed(string name) =>
        Services.Any(descriptor =>
            descriptor.ServiceType == typeof(NuplaneFeedRegistration)
            && descriptor.ImplementationInstance is NuplaneFeedRegistration registration
            && string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool HasExplicitUnrestrictedPackageSelection(NuplaneFeedBuilder feed) =>
        feed.IncludePatterns.Any(static pattern => string.Equals(pattern, "*", StringComparison.Ordinal));

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
