using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageAssemblyProvider"/> that materializes package assemblies
/// from collectible package-specific assembly load contexts.
/// </summary>
public sealed class PackageAssemblyProvider : IPackageAssemblyProvider
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
            if (assembliesByPath.TryGetValue(candidate.AssemblyPath, out var existingAssembly))
            {
                materialized.Add(existingAssembly);
                continue;
            }

            try
            {
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

