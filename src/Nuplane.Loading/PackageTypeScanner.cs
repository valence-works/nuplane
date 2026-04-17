using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Default implementation of <see cref="IPackageTypeScanner"/> that inspects assemblies
/// loaded into package-specific assembly load contexts as a convenience layer over package assembly access.
/// </summary>
internal sealed class PackageTypeScanner : IPackageTypeScanner
{
    private readonly IPackageAssemblyCatalog _packageAssemblyCatalog;
    private readonly IPackageAssemblyProvider _packageAssemblyProvider;
    private readonly ILogger<PackageTypeScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PackageTypeScanner"/>.
    /// </summary>
    /// <param name="packageAssemblyCatalog">The package assembly catalog used to resolve the active loaded package version.</param>
    /// <param name="packageAssemblyProvider">The package assembly provider used to materialize assemblies for exact package-version scans.</param>
    /// <param name="logger">The logger used to report best-effort scan skips.</param>
    public PackageTypeScanner(IPackageAssemblyCatalog packageAssemblyCatalog, IPackageAssemblyProvider packageAssemblyProvider, ILogger<PackageTypeScanner>? logger = null)
    {
        _packageAssemblyCatalog = packageAssemblyCatalog ?? throw new ArgumentNullException(nameof(packageAssemblyCatalog));
        _packageAssemblyProvider = packageAssemblyProvider ?? throw new ArgumentNullException(nameof(packageAssemblyProvider));
        _logger = logger ?? NullLogger<PackageTypeScanner>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Type>> FindTypesAsync<TInterface>(string packageId, CancellationToken cancellationToken)
        => FindTypesAsync(typeof(TInterface), packageId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Type>> FindTypesAsync(Type interfaceType, string packageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var package = await _packageAssemblyCatalog.GetPackagedAssembliesAsync(packageId, cancellationToken);
        return package is null
            ? []
            : ScanAssemblies(interfaceType, package.Assemblies, package.PackageId, package.Version);
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

        return ScanAssemblies(interfaceType, _packageAssemblyProvider.GetAssemblies(packageId, version), packageId, version);
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