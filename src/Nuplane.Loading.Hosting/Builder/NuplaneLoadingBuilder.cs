using Nuplane.Loading;

namespace Nuplane.Loading.Hosting.Builder;

/// <summary>
/// Fluent builder for configuring the Nuplane assembly loading subsystem.
/// Obtain an instance via <see cref="NuplaneBuilderLoadingExtensions.AutoloadPackages"/>.
/// </summary>
public sealed class NuplaneLoadingBuilder
{
    internal bool Enabled { get; private set; } = true;
    internal TimeSpan DeactivationTimeout { get; private set; } = TimeSpan.FromSeconds(15);
    internal List<SharedAssemblyIdentity> SharedAssemblies { get; } = [];

    /// <summary>
    /// Registers a shared assembly whose types are resolved from the host's default
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> rather than from each
    /// package-specific context.
    /// </summary>
    /// <param name="name">The assembly name (without file extension).</param>
    /// <param name="publicKeyToken">The lowercase hex public key token, or an empty string for unsigned assemblies.</param>
    /// <param name="majorVersion">The major version that must match for the shared binding to apply.</param>
    public NuplaneLoadingBuilder SharedAssembly(string name, string publicKeyToken, int majorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        SharedAssemblies.Add(new SharedAssemblyIdentity(name, publicKeyToken, majorVersion));
        return this;
    }

    /// <summary>
    /// Sets the maximum time to wait for a package's assembly load context to drain
    /// active references before forcibly unloading.
    /// </summary>
    /// <param name="timeout">The maximum deactivation timeout.</param>
    public NuplaneLoadingBuilder WithDeactivationTimeout(TimeSpan timeout)
    {
        DeactivationTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Disables assembly loading entirely. When disabled, <see cref="PackageAutoLoadingObserver"/>
    /// silently skips all load requests.
    /// </summary>
    public NuplaneLoadingBuilder Disable()
    {
        Enabled = false;
        return this;
    }
}
