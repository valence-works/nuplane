using Microsoft.Extensions.Options;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Maintenance;

internal sealed class PackageStoreMaintenanceOptionsValidator : IValidateOptions<PackageStoreMaintenanceOptions>
{
    private readonly ITrustedPackageStoreRootCatalogDefinition _rootCatalogDefinition;

    public PackageStoreMaintenanceOptionsValidator(ITrustedPackageStoreRootCatalogDefinition rootCatalogDefinition)
    {
        ArgumentNullException.ThrowIfNull(rootCatalogDefinition);
        _rootCatalogDefinition = rootCatalogDefinition;
    }

    public ValidateOptionsResult Validate(string? name, PackageStoreMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var rootLabel = options.RootLabel;

        if (string.IsNullOrWhiteSpace(rootLabel))
        {
            errors.Add("PackageStoreMaintenance RootLabel must not be null or blank.");
        }
        else if (!string.Equals(rootLabel, rootLabel.Trim(), StringComparison.Ordinal))
        {
            errors.Add("PackageStoreMaintenance RootLabel must not contain leading or trailing whitespace.");
        }

        if (rootLabel is not null && _rootCatalogDefinition.AdditionalRoots.Any(root =>
                string.Equals(root.Label, rootLabel, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"PackageStoreMaintenance RootLabel '{rootLabel}' conflicts with a configured package-store root label.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
