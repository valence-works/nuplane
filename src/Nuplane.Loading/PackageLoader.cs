using System.Collections.Concurrent;
using System.Reflection;
using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Loads package assemblies into isolated collectible load contexts, tracking sessions
/// and providing context removal for unloading. Resolves the main assembly within
/// each package's install directory.
/// </summary>
public sealed class PackageLoader : IPackageLoader
{
    private readonly SharedAssemblyPolicyMatcher _matcher;
    private readonly ConcurrentDictionary<string, PackageAssemblyLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageLoadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="PackageLoader"/> with an optional shared assembly policy matcher.
    /// </summary>
    public PackageLoader(SharedAssemblyPolicyMatcher? matcher = null)
    {
        _matcher = matcher ?? new SharedAssemblyPolicyMatcher();
    }

    /// <summary>
    /// Gets the active load sessions keyed by package-version key.
    /// </summary>
    public IReadOnlyDictionary<string, PackageLoadSession> Sessions => _sessions;

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
                var mainAssemblyPath = ResolveMainAssemblyPath(package.InstallPath);
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
                    LastError: null);

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
                    LastError: ex.Message);
            }
        }

        return Task.FromResult<PackageLoadResult>(new(loaded, failed));
    }

    /// <inheritdoc />
    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        _sessions.TryRemove(key, out _);

        if (_contexts.TryRemove(key, out var removed) && removed is not null)
        {
            context = new(key, removed);
            return true;
        }

        context = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        if (_contexts.TryGetValue(key, out var existing) && existing is not null)
        {
            context = new(key, existing);
            return true;
        }

        context = null;
        return false;
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    private static string ResolveMainAssemblyPath(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            throw new DirectoryNotFoundException($"Install path '{installPath}' does not exist.");
        }

        // Prefer assemblies under a conventional "lib" folder (e.g., lib/<tfm>/) if present.
        var libPath = Path.Combine(installPath, "lib");
        var searchRoot = Directory.Exists(libPath) ? libPath : installPath;

        var assemblies = Directory
            .EnumerateFiles(searchRoot, "*.dll", SearchOption.AllDirectories)
            .ToArray();

        if (assemblies.Length == 0)
        {
            throw new FileNotFoundException($"No loadable assembly found under '{installPath}'.");
        }

        // If there is only a single assembly, use it directly.
        if (assemblies.Length == 1)
        {
            return assemblies[0];
        }

        // Try to select an assembly whose file name matches the package directory name.
        var packageDirectoryName = Path.GetFileName(Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(packageDirectoryName))
        {
            var matchingByName = assemblies
                .Where(path =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        packageDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matchingByName.Length == 1)
            {
                return matchingByName[0];
            }
        }

        // Ambiguous case: multiple candidate assemblies and no clear main assembly.
        throw new InvalidOperationException(
            $"Multiple assemblies were found under '{installPath}', and a main assembly could not be determined. " +
            "Ensure the package contains a single loadable assembly or that the main assembly file name matches the package directory name.");
    }
}
