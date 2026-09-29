using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageAssemblyProvider"/> that materializes package assemblies
/// from collectible package-specific assembly load contexts.
/// </summary>
internal sealed class PackageAssemblyProvider : IPackageAssemblyProvider
{
    private readonly PackageLoader _packageLoader;
    private readonly ILogger<PackageAssemblyProvider> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageAssemblyProvider"/>.
    /// </summary>
    /// <param name="packageLoader">The package loader used to resolve active load contexts and deterministic scan candidates.</param>
    /// <param name="logger">The logger used to report best-effort assembly materialization skips.</param>
    public PackageAssemblyProvider(PackageLoader packageLoader, ILogger<PackageAssemblyProvider>? logger = null)
    {
        _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
        _logger = logger ?? NullLogger<PackageAssemblyProvider>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<Assembly> GetAssemblies(string packageId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!_packageLoader.TryGetContext(packageId, version, out var contextHandle) ||
            contextHandle?.Context is not AssemblyLoadContext loadContext)
        {
            return [];
        }

        if (!TryGetInstallPath(packageId, version, out var installPath))
        {
            return OrderAssemblies(loadContext.Assemblies.ToArray());
        }

        IReadOnlyList<AssemblyScanCandidate> candidates;
        try
        {
            candidates = _packageLoader.BuildScanCandidates(packageId, installPath);
        }
        catch (Exception ex) when (IsSkippableCandidateProjectionException(ex))
        {
            _logger.LogWarning(
                ex,
                "Falling back to already loaded assemblies for package {PackageId}@{Version} because deterministic scan candidates could not be projected from {InstallPath}.",
                packageId,
                version,
                installPath);

            return OrderAssemblies(loadContext.Assemblies.ToArray());
        }

        var assembliesByPath = loadContext.Assemblies
            .Where(static assembly => !string.IsNullOrWhiteSpace(assembly.Location))
            .GroupBy(static assembly => assembly.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var materialized = new List<Assembly>();
        foreach (var candidate in candidates.OrderBy(static candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (IsSharedWithHost(loadContext, candidate.AssemblyPath, packageId, version))
                {
                    continue;
                }

                if (assembliesByPath.TryGetValue(candidate.AssemblyPath, out var existingAssembly))
                {
                    materialized.Add(existingAssembly);
                    continue;
                }

                var loadedAssembly = loadContext.LoadFromAssemblyPath(candidate.AssemblyPath);
                assembliesByPath[candidate.AssemblyPath] = loadedAssembly;
                materialized.Add(loadedAssembly);
            }
            catch (Exception ex) when (IsSkippableAssemblyMaterializationException(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Skipping assembly candidate {AssemblyPath} while materializing assemblies for package {PackageId}@{Version}.",
                    candidate.AssemblyPath,
                    packageId,
                    version);
            }
        }

        return OrderAssemblies(materialized);
    }

    /// <summary>
    /// Determines whether one of the package's assembly files is a shared assembly the context's policy matches.
    /// Such a file is the host's, not the package's, so it is left out of the package's assemblies: returning the
    /// host's copy would have a consumer that scans the package's assemblies treat the host's assembly as the
    /// package's own, and loading the package's file by path would put a private copy into the context, which it
    /// would then hand to the package's code as well. A host with no satisfying copy is reported.
    /// </summary>
    private bool IsSharedWithHost(AssemblyLoadContext loadContext, string assemblyPath, string packageId, string version)
    {
        if (loadContext is not ISharedAssemblyPolicyLoadContext policyContext)
        {
            return false;
        }

        var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
        if (!policyContext.IsSharedAssembly(assemblyName))
        {
            return false;
        }

        if (!SharedAssemblyHostCopy.HostSatisfies(assemblyName))
        {
            _logger.LogWarning(
                "Skipping shared assembly {AssemblyPath} of package {PackageId}@{Version}: the host has no copy of {PolicyEntry} that satisfies the shared-assembly policy.",
                assemblyPath,
                packageId,
                version,
                SharedAssemblyHostCopy.DescribePolicyEntry(assemblyName));
        }

        return true;
    }

    private bool TryGetInstallPath(string packageId, string version, out string installPath)
    {
        var sessionKey = $"{packageId}@{version}";
        if (_packageLoader.Sessions.TryGetValue(sessionKey, out var session) && session.IsLoaded)
        {
            installPath = session.ActiveInstallPath;
            return true;
        }

        installPath = string.Empty;
        return false;
    }

    private static IReadOnlyList<Assembly> OrderAssemblies(IEnumerable<Assembly> assemblies)
        => assemblies
            .OrderBy(static assembly => GetAssemblyPathSortKey(assembly), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static assembly => assembly.FullName ?? assembly.GetName().Name ?? "<unknown>", StringComparer.Ordinal)
            .ToArray();

    private static string GetAssemblyPathSortKey(Assembly assembly)
        => string.IsNullOrWhiteSpace(assembly.Location)
            ? assembly.FullName ?? assembly.GetName().Name ?? string.Empty
            : assembly.Location;

    private static bool IsSkippableCandidateProjectionException(Exception ex) =>
        ex is DirectoryNotFoundException
            or FileNotFoundException
            or InvalidOperationException;

    private static bool IsSkippableAssemblyMaterializationException(Exception ex) =>
        ex is FileNotFoundException
            or FileLoadException
            or BadImageFormatException
            or InvalidOperationException;
}

