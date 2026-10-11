using Microsoft.Extensions.Options;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Maintenance;

internal interface IPackageStoreRootResolver
{
    bool TryResolve(string? label, out TrustedPackageStoreRoot? root);
}

/// <summary>Resolves maintenance aliases against the immutable configured package-store catalog.</summary>
internal sealed class PackageStoreRootResolver : IPackageStoreRootResolver
{
    private readonly Dictionary<string, TrustedPackageStoreRoot> _rootsByLabel;

    public PackageStoreRootResolver(
        ITrustedPackageStoreRootCatalog catalog,
        IOptions<PackageStoreMaintenanceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        var configuredRootLabel = options.Value.RootLabel;
        _rootsByLabel = new(StringComparer.OrdinalIgnoreCase);

        foreach (var root in catalog.Roots)
        {
            var label = string.Equals(root.Label, "default", StringComparison.OrdinalIgnoreCase)
                ? configuredRootLabel
                : root.Label;
            if (label is not null)
            {
                _rootsByLabel.Add(label, root);
            }
        }
    }

    public bool TryResolve(string? label, out TrustedPackageStoreRoot? root)
    {
        if (label is not null && _rootsByLabel.TryGetValue(label, out var resolvedRoot))
        {
            root = resolvedRoot;
            return true;
        }

        root = null;
        return false;
    }
}
