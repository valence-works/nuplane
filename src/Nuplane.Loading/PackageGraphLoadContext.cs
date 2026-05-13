using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Nuplane.Loading;

internal class PackageGraphLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, string> assemblyPathsByName;
    private readonly IReadOnlyList<AssemblyDependencyResolver> dependencyResolvers;
    private readonly IReadOnlyList<string> packageInstallPaths;
    private readonly IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy;
    private readonly SharedAssemblyPolicyMatcher matcher;

    public PackageGraphLoadContext(
        string contextName,
        IReadOnlyList<string> mainAssemblyPaths,
        IReadOnlyList<string> packageInstallPaths,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        SharedAssemblyPolicyMatcher matcher)
        : this(contextName, mainAssemblyPaths, packageInstallPaths, sharedPolicy, matcher, isCollectible: true)
    {
    }

    protected PackageGraphLoadContext(
        string contextName,
        IReadOnlyList<string> mainAssemblyPaths,
        IReadOnlyList<string> packageInstallPaths,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        SharedAssemblyPolicyMatcher matcher,
        bool isCollectible)
        : base(contextName, isCollectible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentNullException.ThrowIfNull(mainAssemblyPaths);

        this.sharedPolicy = sharedPolicy ?? throw new ArgumentNullException(nameof(sharedPolicy));
        this.matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        this.packageInstallPaths = packageInstallPaths ?? throw new ArgumentNullException(nameof(packageInstallPaths));
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

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var resolver in dependencyResolvers)
        {
            var resolvedPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return LoadUnmanagedDllFromPath(resolvedPath);
            }
        }

        var nativePath = ResolveNativeLibraryPath(packageInstallPaths, unmanagedDllName, RuntimeInformation.RuntimeIdentifier);
        return nativePath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(nativePath);
    }

    internal static string? ResolveNativeLibraryPath(
        IEnumerable<string> packageInstallPaths,
        string unmanagedDllName,
        string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName) || string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            return null;
        }

        var fileNames = BuildNativeLibraryFileNames(unmanagedDllName).ToArray();
        foreach (var directory in packageInstallPaths.SelectMany(path => ResolveNativeSearchDirectories(path, runtimeIdentifier)))
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> ResolveNativeSearchDirectories(string packageInstallPath, string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(packageInstallPath) || !Directory.Exists(packageInstallPath))
        {
            yield break;
        }

        yield return packageInstallPath;

        var nativeDirectory = Path.Combine(packageInstallPath, "runtimes", runtimeIdentifier, "native");
        if (Directory.Exists(nativeDirectory))
        {
            yield return nativeDirectory;
        }
    }

    private static IEnumerable<string> BuildNativeLibraryFileNames(string unmanagedDllName)
    {
        yield return unmanagedDllName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{unmanagedDllName}.dll";
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (!unmanagedDllName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{unmanagedDllName}.dylib";
            }

            if (!unmanagedDllName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"lib{unmanagedDllName}.dylib";
            }

            yield break;
        }

        if (!unmanagedDllName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{unmanagedDllName}.so";
        }

        if (!unmanagedDllName.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"lib{unmanagedDllName}.so";
        }
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
