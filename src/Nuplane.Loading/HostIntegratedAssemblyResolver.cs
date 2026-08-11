using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Resolves host-integrated package assemblies for framework by-name assembly requests.
/// </summary>
internal sealed class HostIntegratedAssemblyResolver : IDisposable
{
    private readonly HostIntegratedAssemblyResolutionCatalog _catalog;
    private readonly ILogger<HostIntegratedAssemblyResolver> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="HostIntegratedAssemblyResolver"/>.
    /// </summary>
    public HostIntegratedAssemblyResolver(
        HostIntegratedAssemblyResolutionCatalog catalog,
        ILogger<HostIntegratedAssemblyResolver>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<HostIntegratedAssemblyResolver>.Instance;
        AssemblyLoadContext.Default.Resolving += ResolveFromHostIntegratedPackages;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving -= ResolveFromHostIntegratedPackages;
        _disposed = true;
    }

    private Assembly? ResolveFromHostIntegratedPackages(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (_catalog.TryResolve(assemblyName, out var assembly, out var diagnostic))
        {
            _logger.LogDebug(
                "Resolved host-integrated assembly {AssemblyName} from {AssemblyPath}.",
                diagnostic.RequestedAssemblyName,
                diagnostic.SelectedAssemblyPath);
            return assembly;
        }

        _logger.LogDebug(
            "Host-integrated assembly resolution did not resolve {AssemblyName}. Outcome={Outcome} Message={Message}",
            diagnostic.RequestedAssemblyName,
            diagnostic.Outcome,
            diagnostic.Message);
        return null;
    }
}
