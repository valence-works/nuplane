using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nuplane.Loading.Hosting.Builder;

/// <summary>
/// Fluent builder for configuring Nuplane's optional runtime assembly loading subsystem,
/// including shared assemblies, enablement, and deactivation timeout behavior.
/// </summary>
public sealed class NuplaneLoadingBuilder
{
    private IServiceCollection Services { get; }

    internal NuplaneLoadingBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Registers a shared assembly whose types are resolved from the host's default
    /// <see cref="AssemblyLoadContext"/> rather than from each
    /// package-specific context.
    /// </summary>
    /// <param name="name">The assembly name (without file extension).</param>
    /// <param name="publicKeyToken">The lowercase hex public key token, or an empty string for unsigned assemblies.</param>
    /// <param name="majorVersion">The major version that must match for the shared binding to apply.</param>
    public NuplaneLoadingBuilder SharedAssembly(string name, string publicKeyToken, int majorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Services.Configure<LoadingOptions>(options =>
        {
            options.SharedAssemblies.Add(new(name, publicKeyToken, majorVersion));
        });

        return this;
    }

    /// <summary>
    /// Sets the maximum time to wait for a package's assembly load context to drain
    /// active references before forcibly unloading.
    /// </summary>
    /// <param name="timeout">The maximum deactivation timeout.</param>
    public NuplaneLoadingBuilder WithDeactivationTimeout(TimeSpan timeout)
    {
        Services.Configure<LoadingOptions>(options =>
        {
            options.DeactivationTimeout = timeout;
        });

        return this;
    }

    /// <summary>
    /// Sets the default package load mode for autoloaded packages.
    /// </summary>
    /// <param name="loadMode">The default load mode to apply when no package-specific override exists.</param>
    public NuplaneLoadingBuilder WithDefaultLoadMode(PackageLoadMode loadMode)
    {
        Services.Configure<LoadingOptions>(options =>
        {
            options.DefaultLoadMode = loadMode;
        });

        return this;
    }

    /// <summary>
    /// Sets the package load-mode selection policy used before falling back to the default load mode.
    /// </summary>
    /// <param name="selectionPolicy">The selection policy to apply.</param>
    public NuplaneLoadingBuilder WithLoadModeSelectionPolicy(PackageLoadModeSelectionPolicy selectionPolicy)
    {
        Services.Configure<LoadingOptions>(options =>
        {
            options.LoadModeSelectionPolicy = selectionPolicy;
        });

        return this;
    }

    /// <summary>
    /// Sets a package-specific load mode override.
    /// </summary>
    /// <param name="packageId">The package identifier to override.</param>
    /// <param name="loadMode">The load mode to use for the package.</param>
    public NuplaneLoadingBuilder PackageLoadMode(string packageId, PackageLoadMode loadMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        packageId = packageId.Trim();

        Services.Configure<LoadingOptions>(options =>
        {
            var existing = options.PackageLoadModes.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                options.PackageLoadModes.Remove(existing);
            }

            options.PackageLoadModes.Add(new()
            {
                PackageId = packageId,
                LoadMode = loadMode
            });
        });

        return this;
    }

    /// <summary>
    /// Registers a package activation gate that is consulted before any package graph is loaded, and can
    /// refuse activation of a graph whose host-side pre-condition is not met. Nothing is registered by
    /// default: with no gate registered, loading behaves exactly as it does without this call.
    /// </summary>
    /// <remarks>
    /// Every registered gate is consulted in registration order and any block blocks the whole graph, which
    /// then surfaces as an ordinary load failure. A gate that throws also blocks the graph. Registering the
    /// same gate type twice registers it once. See <see cref="IPackageActivationGate"/> for the full contract.
    /// </remarks>
    /// <typeparam name="TGate">The gate implementation to register as a singleton.</typeparam>
    public NuplaneLoadingBuilder AddActivationGate<TGate>()
        where TGate : class, IPackageActivationGate
    {
        Services.TryAddSingleton<TGate>();
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPackageActivationGate, TGate>(
            static sp => sp.GetRequiredService<TGate>()));

        return this;
    }

    /// <summary>
    /// Enables assembly loading when it was previously disabled.
    /// Useful for code-based overrides on top of configuration.
    /// </summary>
    public NuplaneLoadingBuilder Enable()
    {
        Services.Configure<LoadingOptions>(options =>
        {
            options.Enabled = true;
        });

        return this;
    }

    /// <summary>
    /// Disables assembly loading entirely. When disabled, <see cref="PackageAutoLoadingObserver"/>
    /// silently skips all load requests.
    /// </summary>
    public NuplaneLoadingBuilder Disable()
    {
        Services.Configure<LoadingOptions>(options =>
        {
            options.Enabled = false;
        });

        return this;
    }
}
