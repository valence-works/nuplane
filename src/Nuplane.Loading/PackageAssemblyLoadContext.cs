using System.Reflection;
using System.Runtime.Loader;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

/// <summary>
/// A collectible assembly load context for an individual package, providing assembly isolation
/// and shared assembly policy support. Assemblies matching the shared policy are loaded from
/// the host's default context to avoid version conflicts.
/// </summary>
public sealed class PackageAssemblyLoadContext : AssemblyLoadContext, ISharedAssemblyPolicyLoadContext
{
    private readonly AssemblyDependencyResolver _dependencyResolver;
    private readonly IReadOnlyList<SharedAssemblyPolicyEntry> _sharedPolicy;
    private readonly SharedAssemblyPolicyMatcher _matcher;

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
        : this(packageMainAssemblyPath, sharedPolicy, matcher, graphLeaseOwner: null)
    {
    }

    internal PackageAssemblyLoadContext(
        string packageMainAssemblyPath,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        SharedAssemblyPolicyMatcher matcher,
        PackageGraphUseLeaseOwner? graphLeaseOwner)
        : base($"nuplane:{Path.GetFileNameWithoutExtension(packageMainAssemblyPath)}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageMainAssemblyPath);
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        ArgumentNullException.ThrowIfNull(matcher);

        graphLeaseOwner?.TransferToLifetime(this, isCollectible: true);
        _dependencyResolver = new(packageMainAssemblyPath);
        _sharedPolicy = sharedPolicy;
        _matcher = matcher;
    }

    /// <inheritdoc />
    bool ISharedAssemblyPolicyLoadContext.IsSharedAssembly(AssemblyName assemblyName) => _matcher.IsMatch(assemblyName, _sharedPolicy);

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_matcher.IsMatch(assemblyName, _sharedPolicy))
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

        var path = _dependencyResolver.ResolveAssemblyToPath(assemblyName);
        return string.IsNullOrWhiteSpace(path) ? null : LoadFromAssemblyPath(path);
    }
}
