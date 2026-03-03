using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

public sealed class PackageAssemblyResolver(
    SharedAssemblyPolicyMatcher matcher)
{
    private readonly SharedAssemblyPolicyMatcher matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));

    public Assembly? Resolve(
        AssemblyName assemblyName,
        AssemblyDependencyResolver dependencyResolver,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        ArgumentNullException.ThrowIfNull(dependencyResolver);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        if (matcher.IsMatch(assemblyName, sharedPolicy))
        {
            return ResolveFromDefaultContext(assemblyName);
        }

        var path = dependencyResolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return AssemblyLoadContext.GetLoadContext(typeof(PackageAssemblyResolver).Assembly) is null
            ? null
            : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static Assembly? ResolveFromDefaultContext(AssemblyName assemblyName)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
            return null;
        }
    }
}
