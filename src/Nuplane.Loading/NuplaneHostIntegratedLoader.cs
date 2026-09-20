using System.Runtime.Loader;
using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Loading;

/// <summary>
/// Offline, dependency-injection-free entry point that loads an already-resolved active package set
/// into assemblies exactly the way <see cref="PackageLoadMode.HostIntegrated"/> loading does inside a
/// running Nuplane host — with no host, no hosted services, no reconciliation, and no feed or network
/// access. The overload taking a <c>store-state.json</c> path is the primary one: it reads the state
/// offline and loads what it records, grouping packages into graphs exactly as the host that wrote the
/// state does.
/// </summary>
/// <remarks>
/// <para>
/// Out-of-process tooling that has to resolve a Nuplane host's module or provider assemblies the way
/// that host's own process resolves them uses this. The assemblies become visible to ordinary by-name
/// resolution — <see cref="Type.GetType(string)"/>, <see cref="System.Reflection.Assembly.Load(System.Reflection.AssemblyName)"/>,
/// and a scan of <see cref="AppDomain.GetAssemblies"/> — through the very same
/// <see cref="AssemblyLoadContext.Default"/> resolving hook a running host installs, driven by the same
/// loader, load-mode selection, asset selection, and assembly-resolution catalog.
/// </para>
/// <para>
/// <b>The load is irreversible for the lifetime of the process.</b> Host-integrated assemblies are
/// loaded into non-collectible load contexts and the resolving hook is never removed, so nothing loaded
/// here can be unloaded, replaced, or hidden again. Use it from short-lived worker processes that exit
/// after doing their work, not from a long-running process that expects to reload a package set.
/// </para>
/// <para>
/// The entry point never writes: it does not touch the store, the state file, completion markers, or
/// the install directories it reads.
/// </para>
/// <para>
/// A load is not re-entrant. It holds a process-wide lock for its duration, so calling it again from
/// inside itself — from an <see cref="IPackageActivationGate"/>, most plausibly — throws
/// <see cref="InvalidOperationException"/> rather than waiting for a lock that can never be released.
/// Concurrent calls from unrelated flows are safe and are serialized.
/// </para>
/// </remarks>
public static class NuplaneHostIntegratedLoader
{
    /// <summary>
    /// Loads the supplied active package set into this process the way a running host's
    /// <see cref="PackageLoadMode.HostIntegrated"/> loading would, and reports the resulting per-package
    /// load state. Prefer <see cref="LoadActivePackagesAsync(string, HostIntegratedLoadOptions, CancellationToken)"/>
    /// when the caller has the state file, because only the state carries the graph activation records
    /// that decide which packages a host loads together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Graph membership from this overload is not always the host's.</b> An
    /// <see cref="ActivePackage"/> carries its graph generation identity but not the store's graph
    /// activation records, so this overload groups packages by graph generation identity. A host groups
    /// by the node sets of every <c>Active</c> graph activation record in the store, merging records that
    /// share a package, and only falls back to graph generation identity when the store holds no active
    /// graph record. The two agree whenever the store holds no active graph record, and whenever every
    /// active graph record's node set matches the generations the descriptors carry — which is the
    /// ordinary case after a single reconcile. They can differ when the store holds several active graph
    /// records that share packages, because a record from an earlier reconcile survives until a newer
    /// graph with the same root set replaces it. Use
    /// <see cref="LoadActivePackagesAsync(string, HostIntegratedLoadOptions, CancellationToken)"/> for
    /// guaranteed parity with the host; use this overload when the caller has already filtered or
    /// assembled the package set itself.
    /// </para>
    /// <para>
    /// Each graph is loaded into one non-collectible context, so a package and its dependencies resolve
    /// each other exactly as they do in the host.
    /// </para>
    /// <para>
    /// Per-package problems are reported, not thrown: a graph whose install path is missing on disk,
    /// which contains no loadable assembly, or which an <see cref="IPackageActivationGate"/> in
    /// <see cref="HostIntegratedLoadOptions.ActivationGates"/> refuses, is reported as an ordinary load
    /// failure for every package in it. Only a malformed request — a null argument, a package with a
    /// blank identity or install path, duplicate package identifiers, or an unrecognized
    /// <see cref="HostIntegratedLoadOptions.TargetFrameworkOverride"/> — throws.
    /// </para>
    /// <para>
    /// Calling this more than once in a process is safe: the resolving hook is installed exactly once,
    /// and a package graph that is already loaded is reported from the load that loaded it rather than
    /// loaded a second time. A graph that some other Nuplane composition in the same process has already
    /// loaded host-integrated — a composed host, typically — is refused as an ordinary load failure
    /// instead of being loaded into a second context, because two copies of the same assemblies in one
    /// process can silently disagree about type identity. For the same reason, asking for a package
    /// graph this process already loaded but for a different
    /// <see cref="HostIntegratedLoadOptions.TargetFrameworkOverride"/> throws: the requested assets can
    /// never be the ones the process holds.
    /// </para>
    /// </remarks>
    /// <param name="packages">
    /// The already-resolved active package set to load, as returned by
    /// <c>NuplaneStore.ReadActivePackagesAsync</c>. An empty set loads nothing and returns an empty
    /// result without installing anything into the process.
    /// </param>
    /// <param name="options">The load options, or <see langword="null"/> to load exactly as a host with default host-integrated configuration does.</param>
    /// <param name="cancellationToken">A token to cancel the load. Cancellation is reported as an <see cref="OperationCanceledException"/>, never as a package load failure.</param>
    /// <returns>The per-package load state, and the failure reason of every package that could not be loaded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="packages"/> contains a <see langword="null"/> entry, an entry with a
    /// blank package identifier, version, or install path, or two entries with the same package
    /// identifier; when <paramref name="options"/> contains a <see langword="null"/> activation gate or
    /// shared assembly; or when <see cref="HostIntegratedLoadOptions.TargetFrameworkOverride"/> is not a
    /// recognized target framework moniker.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a package graph in <paramref name="packages"/> is already loaded into this process for
    /// a different <see cref="HostIntegratedLoadOptions.TargetFrameworkOverride"/>, in which case nothing
    /// is loaded or changed by the rejected call; or when the calling flow is already inside a load, for
    /// example an <see cref="IPackageActivationGate"/> calling back into this method.
    /// </exception>
    public static Task<HostIntegratedLoadResult> LoadActivePackagesAsync(
        IReadOnlyList<ActivePackage> packages,
        HostIntegratedLoadOptions? options = null,
        CancellationToken cancellationToken = default) =>
        LoadAsync(packages, state: null, options, cancellationToken);

    /// <summary>
    /// Reads the persisted store state at <paramref name="stateFilePath"/> and loads the package set it
    /// records into this process the way a running host's <see cref="PackageLoadMode.HostIntegrated"/>
    /// loading would. This is the overload to prefer: it is the only one that can group packages into
    /// graphs exactly as the host does, because grouping needs the state's graph activation records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state file is read strictly read-only and offline, with the same file sharing, missing-file,
    /// corrupt-file, and concurrent-write behavior as
    /// <c>NuplaneStore.ReadActivePackagesAsync</c> — it is that same read. A missing state file loads
    /// nothing and returns an empty result. The packages to load are the state's active packages, derived
    /// through the same single definition a running host's <c>IActivePackageCatalog</c> uses, and the
    /// state is read exactly once, so the package set and the graph records always come from one snapshot.
    /// </para>
    /// <para>
    /// Everything else — load mode, asset selection, activation gates, failure reporting, repeat calls,
    /// and the irreversibility of the load — behaves as described on
    /// <see cref="LoadActivePackagesAsync(IReadOnlyList{ActivePackage}, HostIntegratedLoadOptions, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    /// <param name="stateFilePath">The path to the host's <c>store-state.json</c> file.</param>
    /// <param name="options">The load options, or <see langword="null"/> to load exactly as a host with default host-integrated configuration does.</param>
    /// <param name="cancellationToken">A token to cancel the read and the load.</param>
    /// <returns>The per-package load state, and the failure reason of every package that could not be loaded.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stateFilePath"/> is <see langword="null"/>, empty, or whitespace, or when <paramref name="options"/> is invalid as described on the other overload.</exception>
    /// <exception cref="IOException">Thrown when the state file exists but cannot be opened for reading, for example while a host holds an exclusive lock on it mid-write.</exception>
    /// <exception cref="System.Text.Json.JsonException">Thrown when the state file exists but its content is empty, torn, or otherwise not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">Thrown for the reasons described on the other overload.</exception>
    public static async Task<HostIntegratedLoadResult> LoadActivePackagesAsync(
        string stateFilePath,
        HostIntegratedLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

        var state = await NuplaneStore.ReadStateAsync(stateFilePath, cancellationToken).ConfigureAwait(false);

        return await LoadAsync(NuplaneStore.GetActivePackages(state), state, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the persisted store state from the state file resolved from <paramref name="storeOptions"/>
    /// the same way a running host resolves it, and loads the package set it records. See
    /// <see cref="LoadActivePackagesAsync(string, HostIntegratedLoadOptions, CancellationToken)"/>, which
    /// this delegates to.
    /// </summary>
    /// <param name="storeOptions">The store registry options to resolve the effective state file path from.</param>
    /// <param name="options">The load options, or <see langword="null"/> to load exactly as a host with default host-integrated configuration does.</param>
    /// <param name="cancellationToken">A token to cancel the read and the load.</param>
    /// <returns>The per-package load state, and the failure reason of every package that could not be loaded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storeOptions"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="storeOptions"/> resolves to in-memory persistence, which persists no state file, or for the reasons described on the other overloads.</exception>
    public static async Task<HostIntegratedLoadResult> LoadActivePackagesAsync(
        StoreRegistryOptions storeOptions,
        HostIntegratedLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var state = await NuplaneStore.ReadStateAsync(storeOptions, cancellationToken).ConfigureAwait(false);

        return await LoadAsync(NuplaneStore.GetActivePackages(state), state, options, cancellationToken).ConfigureAwait(false);
    }

    private static Task<HostIntegratedLoadResult> LoadAsync(
        IReadOnlyList<ActivePackage> packages,
        StoreStateRecord? state,
        HostIntegratedLoadOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);

        options ??= new HostIntegratedLoadOptions();
        ValidateOptions(options);
        ValidatePackages(packages);

        return packages.Count == 0
            ? Task.FromResult(new HostIntegratedLoadResult([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
            : HostIntegratedLoadComposition.LoadAsync(packages, state, options, cancellationToken);
    }

    private static void ValidateOptions(HostIntegratedLoadOptions options)
    {
        if (options.TargetFrameworkOverride is not null
            && !PackageLoader.IsSupportedTargetFrameworkMoniker(options.TargetFrameworkOverride))
        {
            throw new ArgumentException(
                $"Target framework override '{options.TargetFrameworkOverride}' is not a target framework moniker Nuplane's asset selection understands. Use a moniker such as 'net8.0', 'netstandard2.0', or 'net48'.",
                nameof(options));
        }

        if (options.ActivationGates.Any(static gate => gate is null))
        {
            throw new ArgumentException("Activation gates must not contain a null entry.", nameof(options));
        }

        if (options.SharedAssemblies.Any(static identity => identity is null))
        {
            throw new ArgumentException("Shared assemblies must not contain a null entry.", nameof(options));
        }
    }

    // A malformed identity cannot be loaded and cannot be reported against either, so it is rejected
    // loudly here. Install paths that are merely absent from disk are not checked: those are an ordinary
    // load failure for their graph, exactly as they are for a host.
    private static void ValidatePackages(IReadOnlyList<ActivePackage> packages)
    {
        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            if (package is null)
            {
                throw new ArgumentException($"Package at index {index} is null.", nameof(packages));
            }

            if (string.IsNullOrWhiteSpace(package.PackageId)
                || string.IsNullOrWhiteSpace(package.Version)
                || string.IsNullOrWhiteSpace(package.InstallPath))
            {
                throw new ArgumentException(
                    $"Package at index {index} must have a package identifier, a version, and an install path.",
                    nameof(packages));
            }
        }

        var duplicateIds = packages
            .GroupBy(static package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new ArgumentException(
                $"An active package set has at most one version of each package, but these package identifiers appear more than once: {string.Join(", ", duplicateIds)}.",
                nameof(packages));
        }
    }
}
