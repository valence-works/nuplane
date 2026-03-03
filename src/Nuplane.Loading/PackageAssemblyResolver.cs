using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

/// <summary>
/// Resolves assembly references for loaded packages, routing shared assemblies to the
/// default context and package-specific assemblies through the dependency resolver.
/// </summary>
public sealed class PackageAssemblyResolver(
    SharedAssemblyPolicyMatcher matcher)
{
    private readonly SharedAssemblyPolicyMatcher matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));

    /// <summary>
    /// Resolves an assembly reference, returning the assembly from the default context
    /// if it matches the shared policy, or from the package's dependency resolver otherwise.
    /// </summary>
    /// <param name="assemblyName">The assembly name to resolve.</param>
    /// <param name="dependencyResolver">The package's dependency resolver.</param>
    /// <param name="sharedPolicy">The shared assembly policy entries.</param>
    /// <returns>The resolved assembly, or <see langword="null"/> if resolution fails.</returns>
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

        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(PackageAssemblyResolver).Assembly);
        return loadContext?.LoadFromAssemblyPath(path);
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
