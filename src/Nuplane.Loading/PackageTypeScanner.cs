using System.Runtime.Loader;
using System.Reflection;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageTypeScanner"/> that inspects assemblies
/// loaded into package-specific assembly load contexts.
/// </summary>
public sealed class PackageTypeScanner : IPackageTypeScanner
{
    private readonly IPackageLoader packageLoader;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageTypeScanner"/>.
    /// </summary>
    /// <param name="packageLoader">The package loader used to resolve active load contexts.</param>
    public PackageTypeScanner(IPackageLoader packageLoader)
    {
        this.packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
    }

    /// <inheritdoc />
    public IReadOnlyList<Type> FindTypes<TInterface>(string packageId, string version)
        => FindTypes(typeof(TInterface), packageId, version);

    /// <inheritdoc />
    public IReadOnlyList<Type> FindTypes(Type interfaceType, string packageId, string version)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!packageLoader.TryGetContext(packageId, version, out var contextHandle) ||
            contextHandle?.Context is not AssemblyLoadContext loadContext)
        {
            return Array.Empty<Type>();
        }

        var discovered = new List<Type>();

        foreach (var assembly in loadContext.Assemblies.ToArray())
        {
            Type[] candidates;
            try
            {
                candidates = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                candidates = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in candidates)
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (interfaceType.IsAssignableFrom(type))
                {
                    discovered.Add(type);
                }
            }
        }

        return discovered;
    }
}