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
    private readonly HostIntegratedAssemblyResolutionCatalog catalog;
    private readonly ILogger<HostIntegratedAssemblyResolver> logger;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="HostIntegratedAssemblyResolver"/>.
    /// </summary>
    public HostIntegratedAssemblyResolver(
        HostIntegratedAssemblyResolutionCatalog catalog,
        ILogger<HostIntegratedAssemblyResolver>? logger = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.logger = logger ?? NullLogger<HostIntegratedAssemblyResolver>.Instance;
        AssemblyLoadContext.Default.Resolving += ResolveFromHostIntegratedPackages;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving -= ResolveFromHostIntegratedPackages;
        disposed = true;
    }

    private Assembly? ResolveFromHostIntegratedPackages(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (catalog.TryResolve(assemblyName, out var assembly, out var diagnostic))
        {
            logger.LogDebug(
                "Resolved host-integrated assembly {AssemblyName} from {AssemblyPath}.",
                diagnostic.RequestedAssemblyName,
                diagnostic.SelectedAssemblyPath);
            return assembly;
        }

        logger.LogDebug(
            "Host-integrated assembly resolution did not resolve {AssemblyName}. Outcome={Outcome} Message={Message}",
            diagnostic.RequestedAssemblyName,
            diagnostic.Outcome,
            diagnostic.Message);
        return null;
    }
}
