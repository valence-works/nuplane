using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

public sealed class PackageAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver dependencyResolver;
    private readonly IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy;
    private readonly SharedAssemblyPolicyMatcher matcher;

    public PackageAssemblyLoadContext(
        string packageMainAssemblyPath,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        SharedAssemblyPolicyMatcher matcher)
        : base($"nuplane:{Path.GetFileNameWithoutExtension(packageMainAssemblyPath)}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageMainAssemblyPath);

        dependencyResolver = new(packageMainAssemblyPath);
        this.sharedPolicy = sharedPolicy ?? throw new ArgumentNullException(nameof(sharedPolicy));
        this.matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
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

        var path = dependencyResolver.ResolveAssemblyToPath(assemblyName);
        return string.IsNullOrWhiteSpace(path) ? null : LoadFromAssemblyPath(path);
    }
}
