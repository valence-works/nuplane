using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Feeds.Builder;
using Nuplane.Feeds.Registration;
using Nuplane.Hosting;
using Nuplane.Reconciliation.Configuration;
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

    /// <summary>
    /// Gets the absolute directory a module-owned builder extension should resolve its own relative
    /// configured paths against instead of the current directory, or <see langword="null"/> when
    /// nothing has set one. Set it with <see cref="UseBasePath"/>; a normally-composed host that
    /// never calls it keeps resolving those paths against its own current directory exactly as
    /// before. <c>Nuplane.Restore.RestoreComposition</c> sets this to
    /// <c>NuplaneRestoreOptions.BasePath</c> for a host-free restore, before its
    /// <c>ConfigureBuilder</c> callback runs — a callback that calls <see cref="UseBasePath"/> itself
    /// overrides the restore's base rather than being overridden by it, since the last call wins.
    /// </summary>
    /// <remarks>
    /// This is a general seam, not a directory-feed-specific one: the core <c>Nuplane</c> package
    /// deliberately does not reference module packages such as <c>Nuplane.Sources.Directory</c>, so it
    /// cannot pass a base path into their registration helpers directly. Exposing it here instead lets
    /// any module-owned builder extension read the same base a host or a host-free restore resolved,
    /// without either one having to forward it itself. Directory feeds are the first, and so far only,
    /// consumer — see
    /// <c>Nuplane.Sources.Directory.Builder.NuplaneBuilderDirectoryExtensions.AddDirectoryFeed</c>.
    /// </remarks>
    public string? BasePath { get; internal set; }

    internal NuplaneBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Sets <see cref="BasePath"/>, the directory a module-owned builder extension resolves its own
    /// relative configured paths against instead of the current directory. A host composing Nuplane
    /// directly calls this with its own content root — for example an ASP.NET Core host's
    /// <c>IHostEnvironment.ContentRootPath</c> — so a relative <c>DirectoryPath</c> anchors to the
    /// host rather than to whatever the process's current directory happens to be when it starts;
    /// see the wiki's "Directory feeds as an offline package source" section. The last call wins: a
    /// host-free restore's own base is set before its <c>ConfigureBuilder</c> callback runs, so a
    /// callback that calls this overrides it, and calling it more than once keeps only the final
    /// value.
    /// </summary>
    /// <param name="absolutePath">An absolute directory.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="absolutePath"/> is null, blank, or not an absolute path.</exception>
    public NuplaneBuilder UseBasePath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathRooted(absolutePath))
        {
            throw new ArgumentException(
                $"{nameof(UseBasePath)} requires an absolute path, but was '{absolutePath}'.",
                nameof(absolutePath));
        }

        BasePath = Path.GetFullPath(absolutePath);
        return this;
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

        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReconciliationHostedService>());
        return this;
    }

    /// <summary>
    /// Registers a named feed as a desired-state source. Call <see cref="NuplaneFeedBuilder.FromUri"/>
    /// inside <paramref name="configure"/> to set the feed location, or use module-owned builder extensions
    /// such as <c>AddDirectoryFeed</c> for source-specific feeds.
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

    /// <summary>
    /// Selects <paramref name="option"/> for the capability named <paramref name="name"/>, the same
    /// selection <c>Nuplane:Capabilities:&lt;name&gt;</c> makes from configuration. Called after
    /// configuration binds, so a builder call always overrides a configured selection for the same
    /// capability, the same "builder calls run last" rule <see cref="WithStateFile"/> and
    /// <see cref="UseInMemoryStore"/> follow.
    /// </summary>
    /// <param name="name">The capability's name, matched case-insensitively.</param>
    /// <param name="option">The option's name, matched case-insensitively against the declaring package's declared options.</param>
    public NuplaneBuilder SelectCapability(string name, string option)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        return SelectCapability(name, new CapabilitySelection { Options = [option] });
    }

    /// <summary>
    /// Selects <paramref name="selection"/> for the capability named <paramref name="name"/>,
    /// including any <see cref="CapabilitySelection.Version"/> or <see cref="CapabilitySelection.Feed"/>
    /// override, the same shape <c>Nuplane:Capabilities:&lt;name&gt;</c>'s object form configures.
    /// See <see cref="SelectCapability(string, string)"/> for the precedence rule.
    /// </summary>
    /// <param name="name">The capability's name, matched case-insensitively.</param>
    /// <param name="selection">The selected option(s) and any overrides.</param>
    public NuplaneBuilder SelectCapability(string name, CapabilitySelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(selection);
        Services.Configure<CapabilityOptions>(options => options.Selections[name] = selection);
        return this;
    }
}
