using Nuplane.Store.State;

namespace Nuplane.Operational;

/// <summary>
/// Contributes package-catalog consistency issues to the operational-state surface.
/// </summary>
internal sealed class PackageCatalogOperationalStateContributor(IStoreRegistry storeRegistry) : IOperationalStateContributor
{
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));

    public async Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken)
    {
        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        var issues = 0;

        foreach (var (packageId, version) in state.ActiveVersionById)
        {
            if (!state.ActivePackageDescriptorsByIdNormalized.TryGetValue(packageId, out var descriptor) ||
                !string.Equals(descriptor.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                issues++;
            }
        }

        return new OperationalStateContribution(
            "package-catalog",
            issues > 0 ? [$"package-catalog-issues:{issues}"] : []);
    }
}

