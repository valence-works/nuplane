using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

internal sealed class PackageGraphLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, string> assemblyPathsByName;
    private readonly IReadOnlyList<AssemblyDependencyResolver> dependencyResolvers;
    private readonly IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy;
    private readonly SharedAssemblyPolicyMatcher matcher;

    public PackageGraphLoadContext(
        string contextName,
        IReadOnlyList<string> mainAssemblyPaths,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        SharedAssemblyPolicyMatcher matcher)
        : base(contextName, isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentNullException.ThrowIfNull(mainAssemblyPaths);

        this.sharedPolicy = sharedPolicy ?? throw new ArgumentNullException(nameof(sharedPolicy));
        this.matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        dependencyResolvers = mainAssemblyPaths.Select(static path => new AssemblyDependencyResolver(path)).ToArray();
        assemblyPathsByName = mainAssemblyPaths
            .SelectMany(path => Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.dll", SearchOption.AllDirectories))
            .Select(static path => new
            {
                Path = path,
                AssemblyName = TryGetManagedAssemblyName(path)
            })
            .Where(static candidate => candidate.AssemblyName is not null)
            .GroupBy(static candidate => candidate.AssemblyName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).First().Path, StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (matcher.IsMatch(assemblyName, sharedPolicy))
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        if (assemblyName.Name is not null && assemblyPathsByName.TryGetValue(assemblyName.Name, out var graphAssemblyPath))
        {
            return LoadFromAssemblyPath(graphAssemblyPath);
        }

        foreach (var resolver in dependencyResolvers)
        {
            var resolvedPath = resolver.ResolveAssemblyToPath(assemblyName);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return LoadFromAssemblyPath(resolvedPath);
            }
        }

        return null;
    }

    private static string? TryGetManagedAssemblyName(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Name ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
