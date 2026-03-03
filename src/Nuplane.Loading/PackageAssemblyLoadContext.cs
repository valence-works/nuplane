using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

/// <summary>
/// A collectible assembly load context for an individual package, providing assembly isolation
/// and shared assembly policy support. Assemblies matching the shared policy are loaded from
/// the host's default context to avoid version conflicts.
/// </summary>
public sealed class PackageAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver dependencyResolver;
    private readonly IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy;
    private readonly SharedAssemblyPolicyMatcher matcher;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageAssemblyLoadContext"/> for the specified package assembly.
    /// </summary>
    /// <param name="packageMainAssemblyPath">The file path to the package's main assembly.</param>
    /// <param name="sharedPolicy">The shared assembly policy entries.</param>
    /// <param name="matcher">The matcher used to evaluate assembly sharing eligibility.</param>
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

    /// <inheritdoc />
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
