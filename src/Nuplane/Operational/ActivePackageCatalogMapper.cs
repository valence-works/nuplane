using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Operational;

internal static class ActivePackageCatalogMapper
{
    public static IReadOnlyDictionary<string, ActivePackageDescriptor> BuildNextDescriptors(
        StoreStateRecord currentState,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        PackageChangeSet changeSet,
        string correlationId,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(nextActiveVersions);
        ArgumentNullException.ThrowIfNull(appliedPackages);
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var descriptors = new Dictionary<string, ActivePackageDescriptor>(
            currentState.ActivePackageDescriptorsByIdNormalized,
            StringComparer.OrdinalIgnoreCase);

        foreach (var removedPackageId in changeSet.Removed)
        {
            descriptors.Remove(removedPackageId);
        }

        var changedPackageIds = changeSet.Added
            .Concat(changeSet.Updated)
            .Select(package => package.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var package in appliedPackages.OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!nextActiveVersions.TryGetValue(package.Id, out var activeVersion) ||
                !string.Equals(activeVersion, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!changedPackageIds.Contains(package.Id) &&
                descriptors.TryGetValue(package.Id, out var existing) &&
                string.Equals(existing.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            descriptors[package.Id] = new ActivePackageDescriptor(
                package.Id,
                package.Version,
                Sanitize(package.FeedName),
                Sanitize(package.SourceName),
                package.InstallPath,
                activatedAtUtc,
                correlationId);
        }

        foreach (var packageId in nextActiveVersions.Keys)
        {
            if (!descriptors.ContainsKey(packageId) &&
                appliedPackages.FirstOrDefault(pkg => string.Equals(pkg.Id, packageId, StringComparison.OrdinalIgnoreCase)) is { } package)
            {
                descriptors[package.Id] = new ActivePackageDescriptor(
                    package.Id,
                    package.Version,
                    Sanitize(package.FeedName),
                    Sanitize(package.SourceName),
                    package.InstallPath,
                    activatedAtUtc,
                    correlationId);
            }
        }

        foreach (var packageId in descriptors.Keys.ToArray())
        {
            if (!nextActiveVersions.ContainsKey(packageId))
            {
                descriptors.Remove(packageId);
            }
        }

        return descriptors;
    }

    public static ActivePackagesSnapshot MapSnapshot(StoreStateRecord state, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var packages = state.ActivePackageDescriptorsByIdNormalized.Values
            .Where(package => state.ActiveVersionById.TryGetValue(package.PackageId, out var version)
                && string.Equals(version, package.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static package => package.ToActivePackage())
            .ToArray();

        return new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            state.UpdatedAt,
            packages,
            correlationId);
    }

    private static string? Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

