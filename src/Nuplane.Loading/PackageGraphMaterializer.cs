using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using static Nuplane.Loading.PackageProjection;

namespace Nuplane.Loading;

/// <summary>Resolves and materializes graph assemblies without publishing load sessions or resolver entries.</summary>
internal sealed class PackageGraphMaterializer(
    PackageAssemblyAssetSelector assetSelector,
    SharedAssemblyPolicyMatcher matcher,
    ILogger<PackageLoader> logger)
{
    internal PackageGraphLoadContext CreateContext(
        string contextKey,
        PackageLoadMode loadMode,
        IReadOnlyList<LoadableGraphPackage> packages,
        IReadOnlyList<string> installPaths,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        PackageGraphUseLeaseOwner? leaseOwner)
    {
        var sortedPackages = OrderLoadablePackages(packages);
        var runtimePaths = sortedPackages.SelectMany(static package => package.Selection.AssemblyPaths)
            .Where(path => !HostRuntimeAssemblyCatalog.Contains(path)).ToArray();
        var resolverPaths = sortedPackages.SelectMany(static package => package.Selection.ResolverAssemblyPaths)
            .Where(path => !HostRuntimeAssemblyCatalog.Contains(path)).ToArray();

        return loadMode == PackageLoadMode.HostIntegrated
            ? new HostIntegratedPackageGraphLoadContext(contextKey, runtimePaths, resolverPaths, installPaths,
                sharedPolicy, matcher, leaseOwner)
            : new PackageGraphLoadContext(contextKey, runtimePaths, resolverPaths, installPaths,
                sharedPolicy, matcher, leaseOwner);
    }

    internal static void LoadAssemblies(
        AssemblyLoadContext context,
        IReadOnlyList<LoadableGraphPackage> packages)
    {
        foreach (var path in OrderLoadablePackages(packages)
                     .SelectMany(static package => package.Selection.LoadAssemblyPaths)
                     .Where(path => !HostRuntimeAssemblyCatalog.Contains(path)))
        {
            context.LoadFromAssemblyName(PackageAssemblyReadCheckpoint.GetAssemblyName(path));
        }
    }

    private static IEnumerable<LoadableGraphPackage> OrderLoadablePackages(IEnumerable<LoadableGraphPackage> packages) =>
        packages.OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase);

    internal GraphPackageResolution ResolvePackages(IReadOnlyList<ResolvedPackage> packages)
    {
        var loadablePackages = new List<LoadableGraphPackage>(packages.Count);
        var skippedPackages = new List<GraphPackage>();
        var hostRuntimePackages = new List<GraphPackage>();
        var failedPackages = new List<GraphPackage>();
        Exception? resolutionFailure = null;

        foreach (var package in packages)
        {
            try
            {
                var selection = assetSelector.Select(package.InstallPath, package.Id);
                if (selection.AssemblyPaths.All(HostRuntimeAssemblyCatalog.Contains))
                {
                    hostRuntimePackages.Add(new(package.Id, package.Version, package.InstallPath));
                    logger.LogInformation(
                        "Skipped package {PackageId}@{Version} in graph because assembly {AssemblyPath} is provided by the host runtime.",
                        package.Id,
                        package.Version,
                        string.Join(", ", selection.AssemblyPaths));
                    continue;
                }

                loadablePackages.Add(new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    selection));
            }
            catch (NoLoadableAssemblyException ex)
            {
                skippedPackages.Add(new(package.Id, package.Version, package.InstallPath));
                logger.LogInformation(
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

        return new(loadablePackages, skippedPackages, hostRuntimePackages, failedPackages, resolutionFailure);
    }

    /// <summary>
    /// Loads and returns each package's own assemblies, which the resolution catalog then publishes for framework
    /// by-name resolution. An assembly the shared-assembly policy matches is not the package's own: it is skipped,
    /// never loaded from the package's file, because loading it by path bypasses the context's <c>Load</c> override
    /// and would give the graph a private copy that the package's code then binds instead of the host's.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<Assembly>> GetHostIntegratedAssemblies(
        AssemblyLoadContext context,
        IReadOnlyList<LoadableGraphPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy)
    {
        var result = new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase);
        var assembliesByPath = context.Assemblies
            .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
            .GroupBy(static assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var packageAssemblies = new List<Assembly>();
            foreach (var candidate in assetSelector.BuildScanCandidates(package.Id, package.InstallPath))
            {
                if (assembliesByPath.TryGetValue(candidate.AssemblyPath, out var existingAssembly))
                {
                    packageAssemblies.Add(existingAssembly);
                    continue;
                }

                try
                {
                    if (IsProvidedByHost(PackageAssemblyReadCheckpoint.GetAssemblyName(candidate.AssemblyPath), package, sharedPolicy))
                    {
                        continue;
                    }

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

    /// <summary>
    /// Builds the entries the graph would publish, for the conflict check that runs before it loads. Skips the
    /// assemblies the shared-assembly policy matches, exactly as <see cref="GetHostIntegratedAssemblies"/>
    /// does, so the graph is checked for what it will actually publish.
    /// </summary>
    internal IReadOnlyList<HostIntegratedAssemblyResolutionCandidate> BuildResolutionCandidates(
        string graphKey,
        IReadOnlyList<LoadableGraphPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy)
    {
        var entries = new List<HostIntegratedAssemblyResolutionCandidate>();
        foreach (var package in packages)
        {
            foreach (var candidate in assetSelector.BuildScanCandidates(package.Id, package.InstallPath))
            {
                try
                {
                    var assemblyName = PackageAssemblyReadCheckpoint.GetAssemblyName(candidate.AssemblyPath);
                    if (IsProvidedByHost(assemblyName, package, sharedPolicy))
                    {
                        continue;
                    }

                    entries.Add(new(
                        assemblyName.Name ?? Path.GetFileNameWithoutExtension(candidate.AssemblyPath),
                        assemblyName.Version,
                        package.Id,
                        package.Version,
                        graphKey,
                        assemblyName.CultureName));
                }
                catch (BadImageFormatException)
                {
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Determines whether <paramref name="assemblyName"/>, a file of <paramref name="package"/>, is a shared
    /// assembly the host provides. Refuses the load when it is but the host has no copy of it that satisfies the
    /// policy, because the package's code would otherwise load and then fail on the missing assembly.
    /// </summary>
    private bool IsProvidedByHost(AssemblyName assemblyName, LoadableGraphPackage package, IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy)
    {
        if (!matcher.IsMatch(assemblyName, sharedPolicy))
        {
            return false;
        }

        if (!SharedAssemblyHostCopy.HostSatisfies(assemblyName))
        {
            throw new FileNotFoundException(
                $"Package '{package.Id}@{package.Version}' carries shared assembly {SharedAssemblyHostCopy.DescribePolicyEntry(assemblyName)}, " +
                "which the shared-assembly policy leaves to the host, but the host has no copy of it with that major version.");
        }

        return true;
    }
}

internal sealed record LoadableGraphPackage(
    string Id,
    string Version,
    string InstallPath,
    AssemblyAssetSelection Selection);

internal sealed record GraphPackage(
    string Id,
    string Version,
    string InstallPath);

internal sealed record GraphPackageResolution(
    IReadOnlyList<LoadableGraphPackage> LoadablePackages,
    IReadOnlyList<GraphPackage> SkippedPackages,
    IReadOnlyList<GraphPackage> HostRuntimePackages,
    IReadOnlyList<GraphPackage> FailedPackages,
    Exception? ResolutionFailure)
{
    /// <summary>
    /// Gets the graph members that were evaluated successfully but contribute no assemblies to load,
    /// either because they contain none or because the host runtime already provides them.
    /// </summary>
    public IEnumerable<GraphPackage> InertPackages => SkippedPackages.Concat(HostRuntimePackages);

    internal void ThrowIfUnusable(string graphKey)
    {
        if (ResolutionFailure is not null)
            throw ResolutionFailure;

        if (LoadablePackages.Count != 0 || (SkippedPackages.Count == 0 && HostRuntimePackages.Count > 0))
            return;

        var paths = SkippedPackages.Count == 0
            ? "<none>"
            : string.Join(", ", SkippedPackages.Select(static package => $"'{package.InstallPath}'"));
        throw new NoLoadableAssemblyException(
            $"No loadable assembly found in graph '{graphKey}'. Scanned install paths: {paths}.");
    }
}

internal static class HostRuntimeAssemblyCatalog
{
    private static readonly Lazy<IReadOnlySet<string>> AssemblyNames = new(LoadAssemblyNames);

    public static bool Contains(string assemblyPath)
    {
        try
        {
            var assemblyName = PackageAssemblyReadCheckpoint.GetAssemblyName(assemblyPath).Name;
            return !string.IsNullOrWhiteSpace(assemblyName) &&
                IsHostFrameworkAssemblyName(assemblyName) &&
                AssemblyNames.Value.Contains(assemblyName);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException)
        {
            return false;
        }
    }

    private static bool IsHostFrameworkAssemblyName(string? assemblyName) =>
        !string.IsNullOrWhiteSpace(assemblyName) &&
        (assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Equals("netstandard", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<string> LoadAssemblyNames()
    {
        var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            return assemblyNames;
        }

        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var assemblyName = PackageAssemblyReadCheckpoint.GetAssemblyName(path).Name;
                if (!string.IsNullOrWhiteSpace(assemblyName))
                {
                    assemblyNames.Add(assemblyName);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException)
            {
            }
        }

        return assemblyNames;
    }
}
