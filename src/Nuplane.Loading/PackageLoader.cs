using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Loads package assemblies into isolated collectible load contexts, tracking sessions
/// and providing context removal for unloading. Resolves the main assembly within
/// each package's install directory.
/// </summary>
internal sealed class PackageLoader : IPackageLoader
{
    private readonly SharedAssemblyPolicyMatcher _matcher;
    private readonly HostIntegratedAssemblyResolutionCatalog _hostIntegratedResolutionCatalog;
    private readonly PackageLoadModeSelector _loadModeSelector;
    private readonly LoadingOptions _options;
    private readonly ILogger<PackageLoader> _logger;
    private readonly HostIntegratedAssemblyResolver? _hostIntegratedAssemblyResolver;
    private readonly ConcurrentDictionary<string, AssemblyLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageLoadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoadedGraphCacheEntry> _loadedGraphs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="PackageLoader"/> with an optional shared assembly policy matcher.
    /// </summary>
    public PackageLoader(
        SharedAssemblyPolicyMatcher? matcher = null,
        HostIntegratedAssemblyResolutionCatalog? hostIntegratedResolutionCatalog = null,
        PackageLoadModeSelector? loadModeSelector = null,
        IOptions<LoadingOptions>? options = null,
        ILogger<PackageLoader>? logger = null,
        HostIntegratedAssemblyResolver? hostIntegratedAssemblyResolver = null)
    {
        _matcher = matcher ?? new SharedAssemblyPolicyMatcher();
        _hostIntegratedResolutionCatalog = hostIntegratedResolutionCatalog ?? new HostIntegratedAssemblyResolutionCatalog();
        _loadModeSelector = loadModeSelector ?? new PackageLoadModeSelector();
        _options = options?.Value ?? new LoadingOptions();
        _logger = logger ?? NullLogger<PackageLoader>.Instance;
        _hostIntegratedAssemblyResolver = hostIntegratedAssemblyResolver;
    }

    /// <summary>
    /// Gets the active load sessions keyed by package-version key.
    /// </summary>
    public IReadOnlyDictionary<string, PackageLoadSession> Sessions => _sessions;

    /// <summary>
    /// Builds deterministic assembly scan candidates for the specified active package install path.
    /// </summary>
    public IReadOnlyList<AssemblyScanCandidate> BuildScanCandidates(string packageId, string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var selection = ResolveAssemblySelection(installPath, packageId, hostTargetFrameworkOverride: null);
        var assemblyPaths = Directory
            .EnumerateFiles(selection.CandidateSearchRoot, "*.dll", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return assemblyPaths
            .Select(path => new AssemblyScanCandidate(
                path,
                Path.GetFileName(path),
                TryResolveTargetFrameworkMoniker(path, installPath),
                string.Equals(path, selection.MainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "PrimaryLoadAssembly"
                    : "AdditionalManagedAssembly",
                string.Equals(path, selection.MainAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    ? "selected-by-loader"
                    : "co-located-managed-assembly"))
            .ToArray();
    }

    /// <inheritdoc />
    public Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        EnsurePackagesLoaded(packages, sharedPolicy, cancellationToken, loaded, failed);

        return Task.FromResult<PackageLoadResult>(new(loaded, failed));
    }

    /// <inheritdoc />
    public Task<PackageLoadResult> EnsureGraphLoadedAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageGraphs);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var loaded = new List<PackageLoadSession>();
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageGraph in packageGraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var graphKey = BuildGraphKey(packageGraph);
            var selectedModes = packageGraph
                .Select(package => _loadModeSelector.Select(package, _options, graphKey))
                .ToArray();
            var selections = PromoteHostIntegratedGraphSelections(selectedModes);

            if (packageGraph.Count <= 1 && selections.All(static selection => selection.LoadMode == PackageLoadMode.Collectible))
            {
                EnsurePackagesLoaded(packageGraph, sharedPolicy, cancellationToken, loaded, failed);
                continue;
            }

            EnsureGraphLoaded(packageGraph, selections, sharedPolicy, loaded, failed);
        }

        return Task.FromResult<PackageLoadResult>(new(loaded, failed));
    }

    private void EnsurePackagesLoaded(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed)
    {
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(package.Id, package.Version);
            if (_sessions.TryGetValue(key, out var existing) && existing.IsLoaded)
            {
                loaded.Add(existing);
                continue;
            }

            try
            {
                var mainAssemblyPath = ResolveMainAssemblyPath(package.InstallPath, package.Id);
                var context = new PackageAssemblyLoadContext(mainAssemblyPath, sharedPolicy, _matcher);
                var assemblyName = AssemblyName.GetAssemblyName(mainAssemblyPath);
                context.LoadFromAssemblyName(assemblyName);

                _contexts[key] = context;

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null,
                    PackageLoadMode.Collectible,
                    FrameworkIntegrationSafe: false);

                _sessions[key] = session;
                loaded.Add(session);
            }
            catch (Exception ex)
            {
                failed[package.Id] = ex.Message;
                _sessions[key] = new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: false,
                    LastError: ex.Message,
                    PackageLoadMode.Collectible,
                    FrameworkIntegrationSafe: false);
            }
        }
    }

    private PackageLoadResult EnsureGraphLoaded(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<PackageLoadModeSelection> selections,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        List<PackageLoadSession> loaded,
        Dictionary<string, string> failed)
    {
        var graphKey = BuildGraphKey(packages);
        var graphLoadMode = selections.Any(static selection => selection.LoadMode == PackageLoadMode.HostIntegrated)
            ? PackageLoadMode.HostIntegrated
            : PackageLoadMode.Collectible;

        if (_loadedGraphs.TryGetValue(graphKey, out var cachedGraph)
            && cachedGraph.LoadMode == graphLoadMode
            && TryGetLoadedGraphSessions(cachedGraph, graphKey, graphLoadMode, out var existingGraphSessions))
        {
            loaded.AddRange(existingGraphSessions);
            return new(loaded, failed);
        }

        AssemblyLoadContext? context = null;
        var replacedContexts = new List<AssemblyLoadContext>();
        var catalogPublished = false;
        GraphPackageResolution? graphPackages = null;

        try
        {
            graphPackages = ResolveGraphPackages(packages);
            if (graphPackages.ResolutionFailure is not null)
            {
                throw graphPackages.ResolutionFailure;
            }

            if (graphPackages.LoadablePackages.Count == 0)
            {
                throw new NoLoadableAssemblyException(
                    $"No loadable assembly found in graph '{graphKey}'. Scanned install paths: {FormatQuotedList(graphPackages.SkippedInstallPaths)}.");
            }

            var mainAssemblyPaths = graphPackages.LoadablePackages
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .Select(static package => package.MainAssemblyPath)
                .ToArray();
            if (graphLoadMode == PackageLoadMode.HostIntegrated)
            {
                var candidates = BuildHostIntegratedResolutionCandidates(graphKey, graphPackages.LoadablePackages);
                _hostIntegratedResolutionCatalog.ValidateCanPublishGraph(graphKey, candidates);
            }

            context = graphLoadMode == PackageLoadMode.HostIntegrated
                ? new HostIntegratedPackageGraphLoadContext(graphKey, mainAssemblyPaths, packages.Select(static package => package.InstallPath).ToArray(), sharedPolicy, _matcher)
                : new PackageGraphLoadContext(graphKey, mainAssemblyPaths, packages.Select(static package => package.InstallPath).ToArray(), sharedPolicy, _matcher);

            foreach (var mainAssemblyPath in mainAssemblyPaths)
            {
                context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(mainAssemblyPath));
            }

            var assembliesByPackageKey = graphLoadMode == PackageLoadMode.HostIntegrated
                ? MaterializeHostIntegratedAssemblies(context, graphPackages.LoadablePackages)
                : new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase);

            if (graphLoadMode == PackageLoadMode.HostIntegrated)
            {
                _hostIntegratedResolutionCatalog.PublishGraph(graphKey, selections, assembliesByPackageKey);
                catalogPublished = true;
            }

            foreach (var package in graphPackages.LoadablePackages)
            {
                var key = BuildKey(package.Id, package.Version);
                if (_contexts.TryGetValue(key, out var previousContext) &&
                    !ReferenceEquals(previousContext, context))
                {
                    replacedContexts.Add(previousContext);
                }

                _contexts[key] = context;

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    graphKey,
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null,
                    graphLoadMode,
                    FrameworkIntegrationSafe: graphLoadMode == PackageLoadMode.HostIntegrated);

                _sessions[key] = session;
                loaded.Add(session);

                _logger.LogInformation(
                    "Loaded package {PackageId}@{Version} with LoadMode={LoadMode} ContextKey={ContextKey}.",
                    package.Id,
                    package.Version,
                    graphLoadMode,
                    graphKey);
            }

            _loadedGraphs[graphKey] = new(
                graphLoadMode,
                graphPackages.LoadablePackages
                    .Select(static package => BuildKey(package.Id, package.Version))
                    .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

            UnloadUnreferencedContexts(replacedContexts);
        }
        catch (Exception ex)
        {
            if (context?.IsCollectible == true)
            {
                context.Unload();
            }

            foreach (var package in ResolveFailedGraphPackages(packages, graphPackages))
            {
                var key = BuildKey(package.Id, package.Version);
                if (context is not null &&
                    _contexts.TryGetValue(key, out var existingContext) &&
                    ReferenceEquals(existingContext, context))
                {
                    _contexts.TryRemove(key, out _);
                }

                if (catalogPublished)
                {
                    _hostIntegratedResolutionCatalog.RemovePackage(package.Id, package.Version);
                }

                failed[package.Id] = ex.Message;
                _sessions[key] = new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    graphKey,
                    DateTimeOffset.UtcNow,
                    IsLoaded: false,
                    LastError: ex.Message,
                    graphLoadMode,
                    FrameworkIntegrationSafe: false);
            }
        }

        return new(loaded, failed);
    }

    private bool TryGetLoadedGraphSessions(
        LoadedGraphCacheEntry cachedGraph,
        string graphKey,
        PackageLoadMode graphLoadMode,
        out IReadOnlyList<PackageLoadSession> existingGraphSessions)
    {
        var sessions = new List<PackageLoadSession>(cachedGraph.LoadablePackageKeys.Count);
        foreach (var key in cachedGraph.LoadablePackageKeys)
        {
            if (!_sessions.TryGetValue(key, out var session)
                || !session.IsLoaded
                || !string.Equals(session.ContextKey, graphKey, StringComparison.OrdinalIgnoreCase)
                || session.LoadMode != graphLoadMode)
            {
                existingGraphSessions = [];
                return false;
            }

            sessions.Add(session);
        }

        existingGraphSessions = sessions;
        return true;
    }

    private GraphPackageResolution ResolveGraphPackages(IReadOnlyList<ResolvedPackage> packages)
    {
        var loadablePackages = new List<LoadableGraphPackage>(packages.Count);
        var skippedPackages = new List<GraphPackage>();
        var failedPackages = new List<GraphPackage>();
        Exception? resolutionFailure = null;

        foreach (var package in packages)
        {
            try
            {
                loadablePackages.Add(new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    ResolveMainAssemblyPath(package.InstallPath, package.Id)));
            }
            catch (NoLoadableAssemblyException ex)
            {
                skippedPackages.Add(new(package.Id, package.Version, package.InstallPath));
                _logger.LogInformation(
                    "Skipped package {PackageId}@{Version} in graph because it contains no loadable assemblies under {InstallPath}. Reason={Reason}",
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    ex.Message);
            }
            catch (Exception ex)
            {
                failedPackages.Add(new(package.Id, package.Version, package.InstallPath));
                resolutionFailure ??= ex;
            }
        }

        return new(loadablePackages, skippedPackages, failedPackages, resolutionFailure);
    }

    private void UnloadUnreferencedContexts(IEnumerable<AssemblyLoadContext> contexts)
    {
        foreach (var context in contexts.Distinct())
        {
            if (context.IsCollectible && !_contexts.Values.Any(existing => ReferenceEquals(existing, context)))
            {
                context.Unload();
            }
        }
    }

    /// <inheritdoc />
    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        _sessions.TryRemove(key, out _);

        if (_contexts.TryRemove(key, out var removed))
        {
            context = new(key, removed);
            _hostIntegratedResolutionCatalog.RemovePackage(packageId, version);
            return true;
        }

        context = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        if (_contexts.TryGetValue(key, out var existing))
        {
            context = new(key, existing);
            return true;
        }

        context = null;
        return false;
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    private static string FormatQuotedList(IReadOnlyCollection<string> values) =>
        values.Count == 0
            ? "<none>"
            : string.Join(", ", values.Select(static value => $"'{value}'"));

    private static IReadOnlyList<GraphPackage> ResolveFailedGraphPackages(
        IReadOnlyList<ResolvedPackage> packages,
        GraphPackageResolution? graphPackages)
    {
        if (graphPackages is null)
        {
            return packages
                .Select(static package => new GraphPackage(package.Id, package.Version, package.InstallPath))
                .ToArray();
        }

        if (graphPackages.LoadablePackages.Count == 0 && graphPackages.FailedPackages.Count == 0)
        {
            return graphPackages.SkippedPackages;
        }

        return graphPackages.LoadablePackages
            .Select(static package => new GraphPackage(package.Id, package.Version, package.InstallPath))
            .Concat(graphPackages.FailedPackages)
            .GroupBy(static package => BuildKey(package.Id, package.Version), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    private static string BuildGraphKey(IReadOnlyList<ResolvedPackage> packages) =>
        "graph:" + string.Join('|', packages
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static package => BuildKey(package.Id, package.Version)));

    private static IReadOnlyList<PackageLoadModeSelection> PromoteHostIntegratedGraphSelections(
        IReadOnlyList<PackageLoadModeSelection> selections)
    {
        if (selections.All(static selection => selection.LoadMode == PackageLoadMode.Collectible))
        {
            return selections;
        }

        return selections
            .Select(static selection => selection.LoadMode == PackageLoadMode.HostIntegrated
                ? selection
                : selection with
                {
                    LoadMode = PackageLoadMode.HostIntegrated,
                    SelectionReason = "dependency-closure"
                })
            .ToArray();
    }

    private IReadOnlyDictionary<string, IReadOnlyList<Assembly>> MaterializeHostIntegratedAssemblies(
        AssemblyLoadContext context,
        IReadOnlyList<LoadableGraphPackage> packages)
    {
        var result = new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase);
        var assembliesByPath = context.Assemblies
            .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
            .GroupBy(static assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var packageAssemblies = new List<Assembly>();
            foreach (var candidate in BuildScanCandidates(package.Id, package.InstallPath))
            {
                if (assembliesByPath.TryGetValue(candidate.AssemblyPath, out var existingAssembly))
                {
                    packageAssemblies.Add(existingAssembly);
                    continue;
                }

                try
                {
                    AssemblyName.GetAssemblyName(candidate.AssemblyPath);
                    var loadedAssembly = context.LoadFromAssemblyPath(candidate.AssemblyPath);
                    assembliesByPath[candidate.AssemblyPath] = loadedAssembly;
                    packageAssemblies.Add(loadedAssembly);
                }
                catch (BadImageFormatException)
                {
                }
            }

            result[BuildKey(package.Id, package.Version)] = packageAssemblies
                .OrderBy(static assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return result;
    }

    private IReadOnlyList<HostIntegratedAssemblyResolutionCandidate> BuildHostIntegratedResolutionCandidates(
        string graphKey,
        IReadOnlyList<LoadableGraphPackage> packages)
    {
        var entries = new List<HostIntegratedAssemblyResolutionCandidate>();
        foreach (var package in packages)
        {
            foreach (var candidate in BuildScanCandidates(package.Id, package.InstallPath))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(candidate.AssemblyPath);
                    entries.Add(new(
                        assemblyName.Name ?? Path.GetFileNameWithoutExtension(candidate.AssemblyPath),
                        assemblyName.Version,
                        package.Id,
                        package.Version,
                        graphKey));
                }
                catch (BadImageFormatException)
                {
                }
            }
        }

        return entries;
    }

    private static string? TryResolveTargetFrameworkMoniker(string assemblyPath, string installPath)
    {
        var relativePath = Path.GetRelativePath(installPath, assemblyPath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length >= 3
            && string.Equals(segments[0], "lib", StringComparison.OrdinalIgnoreCase)
            && TryParseFrameworkTarget(segments[1], out var framework))
        {
            return framework.DisplayName;
        }

        return null;
    }

    private static string ResolveMainAssemblyPath(string installPath, string packageId) =>
        ResolveMainAssemblyPath(installPath, packageId, hostTargetFrameworkOverride: null);

    private static string ResolveMainAssemblyPath(string installPath, string packageId, string? hostTargetFrameworkOverride)
        => ResolveAssemblySelection(installPath, packageId, hostTargetFrameworkOverride).MainAssemblyPath;

    private static AssemblyAssetSelection ResolveAssemblySelection(
        string installPath,
        string packageId,
        string? hostTargetFrameworkOverride)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            throw new DirectoryNotFoundException($"Install path '{installPath}' does not exist.");
        }

        var libPath = Path.Combine(installPath, "lib");
        if (Directory.Exists(libPath) && TryResolveFrameworkSpecificAssemblySelection(libPath, installPath, packageId, hostTargetFrameworkOverride, out var frameworkSpecificSelection))
        {
            return frameworkSpecificSelection;
        }

        var searchRoot = Directory.Exists(libPath) ? libPath : installPath;
        var mainAssemblyPath = ResolveAssemblyFromCandidates(
            Directory.EnumerateFiles(searchRoot, "*.dll", SearchOption.AllDirectories),
            installPath,
            packageId,
            selectedFramework: null);

        return new AssemblyAssetSelection(mainAssemblyPath, searchRoot);
    }

    private static bool TryResolveFrameworkSpecificAssemblySelection(
        string libPath,
        string installPath,
        string packageId,
        string? hostTargetFrameworkOverride,
        out AssemblyAssetSelection selection)
    {
        var frameworkDirectories = Directory
            .EnumerateDirectories(libPath)
            .Select(FrameworkDirectory.Create)
            .Where(candidate => candidate is not null)
            .Cast<FrameworkDirectory>()
            .ToArray();

        if (frameworkDirectories.Length == 0)
        {
            selection = null!;
            return false;
        }

        var hostFramework = ResolveHostFramework(hostTargetFrameworkOverride);
        if (hostFramework is null)
        {
            throw new InvalidOperationException(
                $"Nuplane could not determine the current host target framework while resolving '{packageId}' from '{installPath}'.");
        }

        var selectedDirectory = SelectBestFrameworkDirectory(hostFramework, frameworkDirectories);
        if (selectedDirectory is null)
        {
            var availableFrameworks = string.Join(", ", frameworkDirectories.Select(candidate => candidate.FolderName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"No compatible target framework assets were found under '{installPath}' for host framework '{GetFrameworkDisplayName(hostFramework)}'. Available frameworks: {availableFrameworks}.");
        }

        var mainAssemblyPath = ResolveAssemblyFromCandidates(
            Directory.EnumerateFiles(selectedDirectory.Path, "*.dll", SearchOption.AllDirectories),
            installPath,
            packageId,
            selectedDirectory.FolderName);

        selection = new AssemblyAssetSelection(mainAssemblyPath, selectedDirectory.Path);

        return true;
    }

    private static string ResolveAssemblyFromCandidates(
        IEnumerable<string> candidatePaths,
        string installPath,
        string packageId,
        string? selectedFramework)
    {
        var assemblies = candidatePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (assemblies.Length == 0)
        {
            throw new NoLoadableAssemblyException($"No loadable assembly found under '{installPath}'.");
        }

        if (assemblies.Length == 1)
        {
            return assemblies[0];
        }

        var matchingByPackageId = assemblies
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingByPackageId.Length == 1)
        {
            return matchingByPackageId[0];
        }

        var frameworkSuffix = selectedFramework is null ? string.Empty : $" for target framework '{selectedFramework}'";
        throw new InvalidOperationException(
            $"Multiple assemblies were found under '{installPath}'{frameworkSuffix}, and a main assembly could not be determined for package '{packageId}'. " +
            "Ensure the package contains a single loadable assembly for the selected target framework or that one assembly name matches the package ID.");
    }

    private static FrameworkDirectory? SelectBestFrameworkDirectory(
        FrameworkTarget hostFramework,
        IReadOnlyList<FrameworkDirectory> frameworkDirectories)
    {
        return hostFramework.Kind switch
        {
            FrameworkKind.NetCoreApp =>
                SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetCoreApp, hostFramework.Version)
                ?? SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, GetCompatibleNetStandardVersion(hostFramework)),
            FrameworkKind.NetStandard => SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, hostFramework.Version),
            FrameworkKind.NetFramework =>
                SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetFramework, hostFramework.Version)
                ?? SelectHighestCompatibleFramework(frameworkDirectories, FrameworkKind.NetStandard, new Version(2, 0)),
            _ => null
        };
    }

    private static FrameworkDirectory? SelectHighestCompatibleFramework(
        IReadOnlyList<FrameworkDirectory> frameworkDirectories,
        FrameworkKind kind,
        Version maxVersion)
    {
        return frameworkDirectories
            .Where(candidate => candidate.Target.Kind == kind && candidate.Target.Version.CompareTo(maxVersion) <= 0)
            .OrderByDescending(candidate => candidate.Target.Version)
            .FirstOrDefault();
    }

    private static Version GetCompatibleNetStandardVersion(FrameworkTarget hostFramework) => hostFramework.Kind switch
    {
        FrameworkKind.NetCoreApp => new Version(2, 1),
        FrameworkKind.NetFramework => new Version(2, 0),
        FrameworkKind.NetStandard => hostFramework.Version,
        _ => new Version(0, 0)
    };

    private static FrameworkTarget? GetCurrentHostFramework()
    {
        var targetFrameworkName = typeof(PackageLoader).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName;

        if (string.IsNullOrWhiteSpace(targetFrameworkName))
        {
            targetFrameworkName = AppContext.TargetFrameworkName;
        }

        if (string.IsNullOrWhiteSpace(targetFrameworkName))
        {
            return null;
        }

        var frameworkName = new FrameworkName(targetFrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => new FrameworkTarget(FrameworkKind.NetCoreApp, new Version(frameworkName.Version.Major, frameworkName.Version.Minor), $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}"),
            ".NETStandard" => new FrameworkTarget(FrameworkKind.NetStandard, new Version(frameworkName.Version.Major, frameworkName.Version.Minor), $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}"),
            ".NETFramework" => new FrameworkTarget(FrameworkKind.NetFramework, frameworkName.Version, $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}"),
            _ => null
        };
    }

    private static FrameworkTarget? ResolveHostFramework(string? hostTargetFrameworkOverride)
    {
        if (!string.IsNullOrWhiteSpace(hostTargetFrameworkOverride))
        {
            return TryParseFrameworkTarget(hostTargetFrameworkOverride, out var overriddenFramework)
                ? overriddenFramework
                : null;
        }

        return GetCurrentHostFramework();
    }

    private static string GetFrameworkDisplayName(FrameworkTarget framework) => framework.DisplayName;

    private enum FrameworkKind
    {
        Unknown,
        NetCoreApp,
        NetStandard,
        NetFramework
    }

    private sealed record FrameworkTarget(FrameworkKind Kind, Version Version, string DisplayName);

    private sealed record AssemblyAssetSelection(string MainAssemblyPath, string CandidateSearchRoot);

    private sealed record LoadableGraphPackage(
        string Id,
        string Version,
        string InstallPath,
        string MainAssemblyPath);

    private sealed record GraphPackage(
        string Id,
        string Version,
        string InstallPath);

    private sealed record GraphPackageResolution(
        IReadOnlyList<LoadableGraphPackage> LoadablePackages,
        IReadOnlyList<GraphPackage> SkippedPackages,
        IReadOnlyList<GraphPackage> FailedPackages,
        Exception? ResolutionFailure)
    {
        public IReadOnlyList<string> SkippedInstallPaths => SkippedPackages.Select(static package => package.InstallPath).ToArray();
    }

    private sealed record LoadedGraphCacheEntry(
        PackageLoadMode LoadMode,
        IReadOnlyList<string> LoadablePackageKeys);

    private sealed class NoLoadableAssemblyException(string message) : FileNotFoundException(message);

    private sealed record FrameworkDirectory(string Path, string FolderName, FrameworkTarget Target)
    {
        public static FrameworkDirectory? Create(string path)
        {
            var folderName = System.IO.Path.GetFileName(path);
            return TryParseFrameworkTarget(folderName, out var target)
                ? new FrameworkDirectory(path, folderName, target)
                : null;
        }
    }

    private static bool TryParseFrameworkTarget(string folderName, out FrameworkTarget target)
    {
        if (TryParseNetCoreApp(folderName, out var netCoreApp))
        {
            target = netCoreApp;
            return true;
        }

        if (TryParseNetStandard(folderName, out var netStandard))
        {
            target = netStandard;
            return true;
        }

        if (TryParseNetFramework(folderName, out var netFramework))
        {
            target = netFramework;
            return true;
        }

        target = null!;
        return false;
    }

    private static bool TryParseNetCoreApp(string folderName, out FrameworkTarget target)
    {
        const string prefix = "net";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !folderName.Contains('.'))
        {
            target = null!;
            return false;
        }

        var versionText = folderName[prefix.Length..];
        if (!Version.TryParse(versionText, out var version))
        {
            target = null!;
            return false;
        }

        target = new FrameworkTarget(FrameworkKind.NetCoreApp, new Version(version.Major, version.Minor), $"net{version.Major}.{version.Minor}");
        return true;
    }

    private static bool TryParseNetStandard(string folderName, out FrameworkTarget target)
    {
        const string prefix = "netstandard";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            target = null!;
            return false;
        }

        var versionText = folderName[prefix.Length..];
        if (!Version.TryParse(versionText, out var version))
        {
            target = null!;
            return false;
        }

        target = new FrameworkTarget(FrameworkKind.NetStandard, new Version(version.Major, version.Minor), $"netstandard{version.Major}.{version.Minor}");
        return true;
    }

    private static bool TryParseNetFramework(string folderName, out FrameworkTarget target)
    {
        const string prefix = "net";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folderName.Contains('.'))
        {
            target = null!;
            return false;
        }

        var versionDigits = folderName[prefix.Length..];
        if (versionDigits.Length is < 2 or > 3 || !versionDigits.All(char.IsDigit))
        {
            target = null!;
            return false;
        }

        var major = int.Parse(versionDigits[..1]);
        var minor = int.Parse(versionDigits[1..2]);
        var build = versionDigits.Length == 3 ? int.Parse(versionDigits[2..3]) : -1;
        var version = build >= 0 ? new Version(major, minor, build) : new Version(major, minor);
        target = new FrameworkTarget(FrameworkKind.NetFramework, version, folderName);
        return true;
    }
}
