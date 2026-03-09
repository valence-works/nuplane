using System.Reflection;
using System.Runtime.Loader;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageTypeScanner"/> that inspects assemblies
/// loaded into package-specific assembly load contexts.
/// </summary>
public sealed class PackageTypeScanner : IPackageTypeScanner
{
    private readonly IPackageLoader _packageLoader;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageTypeScanner"/>.
    /// </summary>
    /// <param name="packageLoader">The package loader used to resolve active load contexts.</param>
    public PackageTypeScanner(IPackageLoader packageLoader)
    {
        _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));
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

        if (!_packageLoader.TryGetContext(packageId, version, out var contextHandle) ||
            contextHandle?.Context is not AssemblyLoadContext loadContext)
        {
            return [];
        }

        var discovered = new List<Type>();

        foreach (var assembly in loadContext.Assemblies.ToArray())
        {
            foreach (var type in GetCandidateTypes(assembly))
            {
                if (!CanInspect(type))
                {
                    continue;
                }

                try
                {
                    if (interfaceType.IsAssignableFrom(type))
                    {
                        discovered.Add(type);
                    }
                }
                catch (Exception ex) when (IsSkippableTypeInspectionException(ex))
                {
                    continue;
                }
            }
        }

        return discovered;
    }

    private static IReadOnlyList<Type> GetCandidateTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch (Exception ex) when (IsSkippableAssemblyInspectionException(ex))
        {
            return [];
        }
    }

    private static bool CanInspect(Type type)
    {
        try
        {
            return !type.IsAbstract && !type.IsInterface;
        }
        catch (Exception ex) when (IsSkippableTypeInspectionException(ex))
        {
            return false;
        }
    }

    private static bool IsSkippableAssemblyInspectionException(Exception ex) =>
        ex is FileNotFoundException
            or FileLoadException
            or TypeLoadException
            or BadImageFormatException;

    private static bool IsSkippableTypeInspectionException(Exception ex) =>
        ex is FileNotFoundException
            or FileLoadException
            or TypeLoadException
            or ReflectionTypeLoadException;
}