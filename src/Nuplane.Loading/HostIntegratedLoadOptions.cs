using Microsoft.Extensions.Logging;

namespace Nuplane.Loading;

/// <summary>
/// Options for <see cref="NuplaneHostIntegratedLoader"/>.
/// Every option defaults to what a running Nuplane host does, so the default-constructed instance
/// loads a package set exactly the way a host configured for host-integrated loading would.
/// </summary>
public sealed class HostIntegratedLoadOptions
{
    /// <summary>
    /// Gets or sets the target framework moniker — for example <c>net8.0</c> — used to select package
    /// assets instead of the target framework of the current process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Leave this <see langword="null"/> (the default) to resolve assets for the current process
    /// exactly as a running host does. Set it only when the process reading the package set runs on a
    /// different target framework than the host that installed the packages and the caller wants the
    /// assets that host would have selected.
    /// </para>
    /// <para>
    /// The value must be a moniker Nuplane's asset selection understands (<c>net8.0</c>,
    /// <c>netstandard2.0</c>, <c>net48</c>, ...); an unrecognized value is rejected with an
    /// <see cref="ArgumentException"/> rather than silently ignored. Only the target framework is
    /// overridden. Runtime-identifier-specific assets — both <c>runtimes/&lt;rid&gt;/lib</c> managed
    /// assets and native libraries — are still selected for the runtime identifier of the current
    /// process, because they have to be loadable by it, and Nuplane has no runtime-identifier override.
    /// </para>
    /// </remarks>
    public string? TargetFrameworkOverride { get; set; }

    /// <summary>
    /// Gets the activation gates consulted before any package graph is loaded, in the order they are
    /// added. They behave exactly as gates registered with a running host: see
    /// <see cref="IPackageActivationGate"/>. With none added, loading behaves like a host with no gates
    /// registered.
    /// </summary>
    /// <remarks>
    /// A gate must not call
    /// <see cref="NuplaneHostIntegratedLoader.LoadActivePackagesAsync(IReadOnlyList{Nuplane.Abstractions.ActivePackage}, HostIntegratedLoadOptions, CancellationToken)"/>
    /// or any of its overloads: a load holds a process-wide lock while its gates run, so the nested call
    /// throws <see cref="InvalidOperationException"/> instead of waiting forever. Because gates fail
    /// closed, that throw refuses the graph the gate was evaluating and is reported as an ordinary load
    /// failure naming the gate.
    /// </remarks>
    public IList<IPackageActivationGate> ActivationGates { get; } = [];

    /// <summary>
    /// Gets the shared assembly identities that are resolved from the current process's default
    /// assembly load context instead of from the package graph's own context. Supply the same entries
    /// the host whose package set is being loaded has configured, otherwise the two processes can bind
    /// different copies of a shared assembly.
    /// </summary>
    public IList<SharedAssemblyIdentity> SharedAssemblies { get; } = [];

    /// <summary>
    /// Gets or sets the logger factory used for loading diagnostics. When <see langword="null"/>, the
    /// load emits no logs. Because the assembly-resolution hook is installed once per process, only the
    /// factory supplied to the first call in a process is used for hook diagnostics.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
