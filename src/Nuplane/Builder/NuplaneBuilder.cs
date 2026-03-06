using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;

namespace Nuplane.Builder;

/// <summary>
/// Fluent builder for configuring the Nuplane runtime. Obtain an instance via
/// <see cref="NuplaneServiceCollectionExtensions.AddNuplane(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{NuplaneBuilder})"/>.
/// </summary>
public sealed class NuplaneBuilder
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/>.</summary>
    public IServiceCollection Services { get; }

    internal bool AutomaticReconciliation { get; private set; }
    internal TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(60);
    internal string? StateFilePath { get; private set; }
    internal List<NuplaneFeedBuilder> Feeds { get; } = [];

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
        AutomaticReconciliation = true;
        PollInterval = interval;
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

        var feedBuilder = new NuplaneFeedBuilder(name);
        configure(feedBuilder);
        Feeds.Add(feedBuilder);
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
        StateFilePath = path;
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
