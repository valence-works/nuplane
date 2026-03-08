using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nuplane.Abstractions;
using Nuplane.Feeds.Registration;
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

        if (NuplaneFeedRegistrationServices.HasRegisteredFeed(Services, name))
        {
            throw new InvalidOperationException($"A Nuplane feed named '{name}' has already been registered.");
        }

        var feedBuilder = new NuplaneFeedBuilder(name);
        configure(feedBuilder);

        NuplaneFeedRegistrationServices.Register(Services, feedBuilder);
        NuplaneFeedRegistrationServices.AddRegistrationMarker(Services, feedBuilder);
        return this;
    }

    /// <summary>
    /// Specifies the file path for persisting store state across host restarts.
    /// When not set, state persists to the default <c>.nuplane/store-state.json</c> location.
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
    /// Explicitly disables store state persistence, running the store registry in memory only.
    /// No state file is created or read, and reconciliation state is lost on host restart.
    /// </summary>
    public NuplaneBuilder UseInMemoryStore()
    {
        Services.Configure<StoreRegistryOptions>(options =>
        {
            options.UseInMemoryStore = true;
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
}
