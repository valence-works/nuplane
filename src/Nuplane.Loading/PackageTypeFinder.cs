using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageTypeFinder"/> that inspects assemblies
/// from the current active loaded package version as a secondary convenience over assembly access.
/// </summary>
internal sealed class PackageTypeFinder : IPackageTypeFinder
{
    private readonly IPackageAssemblyCatalog _packageAssemblyCatalog;
    private readonly ILogger<PackageTypeFinder> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageTypeFinder"/>.
    /// </summary>
    public PackageTypeFinder(IPackageAssemblyCatalog packageAssemblyCatalog, ILogger<PackageTypeFinder>? logger = null)
    {
        _packageAssemblyCatalog = packageAssemblyCatalog ?? throw new ArgumentNullException(nameof(packageAssemblyCatalog));
        _logger = logger ?? NullLogger<PackageTypeFinder>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Type>> FindTypesAsync<TInterface>(string packageId, CancellationToken cancellationToken)
        => FindTypesAsync(typeof(TInterface), packageId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Type>> FindTypesAsync(Type interfaceType, string packageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var package = await _packageAssemblyCatalog.GetAssembliesAsync(packageId, cancellationToken).ConfigureAwait(false);
        return package is null
            ? []
            : ScanAssemblies(interfaceType, package.Assemblies, package.PackageId, package.Version);
    }

    private IReadOnlyList<Type> ScanAssemblies(Type interfaceType, IReadOnlyList<Assembly> assemblies, string packageId, string version)
    {
        var discovered = new List<Type>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetCandidateTypes(assembly, packageId, version))
            {
                if (!CanInspect(type, assembly, packageId, version))
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
                    _logger.LogWarning(
                        ex,
                        "Skipping type {TypeName} while scanning package {PackageId}@{Version} from assembly {AssemblyName} because assignability inspection failed.",
                        type.FullName ?? type.Name,
                        packageId,
                        version,
                        assembly.FullName ?? assembly.GetName().Name ?? "<unknown>");
                }
            }
        }

        return discovered;
    }

    private IReadOnlyList<Type> GetCandidateTypes(Assembly assembly, string packageId, string version)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning(
                ex,
                "Partially scanned assembly {AssemblyName} for package {PackageId}@{Version}; {LoaderExceptionCount} exported types could not be loaded. First loader exception: {FirstLoaderExceptionMessage}",
                assembly.FullName ?? assembly.GetName().Name ?? "<unknown>",
                packageId,
                version,
                ex.LoaderExceptions.Length,
                GetFirstLoaderExceptionMessage(ex));
            return ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch (Exception ex) when (IsSkippableAssemblyInspectionException(ex))
        {
            _logger.LogWarning(
                ex,
                "Skipping assembly {AssemblyName} while scanning package {PackageId}@{Version} because exported types could not be inspected.",
                assembly.FullName ?? assembly.GetName().Name ?? "<unknown>",
                packageId,
                version);
            return [];
        }
    }

    private bool CanInspect(Type type, Assembly assembly, string packageId, string version)
    {
        try
        {
            return type is { IsAbstract: false, IsInterface: false };
        }
        catch (Exception ex) when (IsSkippableTypeInspectionException(ex))
        {
            _logger.LogWarning(
                ex,
                "Skipping type {TypeName} while scanning package {PackageId}@{Version} from assembly {AssemblyName} because type metadata could not be inspected.",
                type.FullName ?? type.Name,
                packageId,
                version,
                assembly.FullName ?? assembly.GetName().Name ?? "<unknown>");
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

    private static string GetFirstLoaderExceptionMessage(ReflectionTypeLoadException ex) =>
        ex.LoaderExceptions
            .Select(loaderException => loaderException?.Message)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
        ?? "<no loader exception message available>";
}

