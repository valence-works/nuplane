using System.Collections.Concurrent;
using System.Reflection;
using Nuplane.Abstractions;

namespace Nuplane.Loading;

public sealed class PackageLoader : IPackageLoader
{
    private readonly SharedAssemblyPolicyMatcher matcher;
    private readonly ConcurrentDictionary<string, PackageAssemblyLoadContext> contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PackageLoadSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public PackageLoader(SharedAssemblyPolicyMatcher? matcher = null)
    {
        this.matcher = matcher ?? new SharedAssemblyPolicyMatcher();
    }

    public IReadOnlyDictionary<string, PackageLoadSession> Sessions => sessions;

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
            if (sessions.TryGetValue(key, out var existing) && existing.IsLoaded)
            {
                loaded.Add(existing);
                continue;
            }

            try
            {
                var mainAssemblyPath = ResolveMainAssemblyPath(package.InstallPath);
                var context = new PackageAssemblyLoadContext(mainAssemblyPath, sharedPolicy, matcher);
                var assemblyName = AssemblyName.GetAssemblyName(mainAssemblyPath);
                context.LoadFromAssemblyName(assemblyName);

                contexts[key] = context;

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    key,
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null);

                sessions[key] = session;
                loaded.Add(session);
            }
            catch (Exception ex)
            {
                failed[package.Id] = ex.Message;
                sessions[key] = new(
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

    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        var key = BuildKey(packageId, version);
        sessions.TryRemove(key, out _);

        if (contexts.TryRemove(key, out var removed) && removed is not null)
        {
            context = new PackageLoadContextHandle(key, removed);
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

        var assemblyPath = Directory
            .EnumerateFiles(installPath, "*.dll", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new FileNotFoundException($"No loadable assembly found under '{installPath}'.");
        }

        return assemblyPath;
    }
}
